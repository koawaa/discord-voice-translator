using System.Diagnostics;
using System.Text;
using DiscordVoiceTranslator.Core.Vad;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace DiscordVoiceTranslator.Core.Transcription;

public sealed record WhisperOptions
{
    public required string ModelPath { get; init; }
    public bool UseGpu { get; init; } = true;
    public int Threads { get; init; } = 4;

    /// <summary>
    /// Langues que Whisper a le droit de choisir. Évite les fausses détections (une phrase
    /// courte en espagnol prise pour du portugais, etc.). Une seule langue = pas de détection.
    /// </summary>
    public IReadOnlyList<string> AllowedLanguages { get; init; } = ["en", "fr", "es", "de"];
}

/// <summary>
/// Whisper local via whisper.cpp. Le modèle est chargé une seule fois ; les segments sont
/// traités un par un, même quand plusieurs sources audio (Discord, un jeu) le partagent.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    // whisper.cpp refuse ou transcrit mal en dessous d'une seconde : on complète par du silence.
    private const int MinSamples = SileroVad.SampleRate * 11 / 10;

    // Whisper ne lit que les ~220 premiers jetons du prompt : on s'arrête bien avant.
    private const int MaxPromptChars = 500;

    private readonly WhisperFactory _factory;
    private readonly int _threads;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly string[] _allowedLanguages;
    private WhisperProcessor _processor;
    private string _prompt = "";
    private volatile string _wantedPrompt = "";
    private HashSet<string> _promptWords = [];

    public string Description { get; }

    public WhisperTranscriber(WhisperOptions options)
    {
        _allowedLanguages = options.AllowedLanguages.ToArray();
        _threads = options.Threads;
        AddCudaToPath();
        _factory = WhisperFactory.FromPath(options.ModelPath, new WhisperFactoryOptions
        {
            UseGpu = options.UseGpu,
            // Attention « flash » : moins de VRAM pour les tampons de calcul, et plus rapide.
            UseFlashAttention = true,
        });
        _processor = BuildProcessor("");

        Description = $"Whisper {Path.GetFileNameWithoutExtension(options.ModelPath)}, {RuntimeOptions.LoadedLibrary?.ToString() ?? "?"}";
    }

    /// <summary>
    /// Mots que Whisper doit s'attendre à entendre (pseudos du salon, termes du jeu), du plus
    /// au moins important. Pris en compte à la phrase suivante.
    /// </summary>
    public void SetVocabulary(IReadOnlyList<string> terms)
    {
        var kept = new List<string>();
        int length = 0;
        foreach (string term in terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (length + term.Length + 2 > MaxPromptChars)
                break;
            kept.Add(term);
            length += term.Length + 2;
        }
        _wantedPrompt = kept.Count == 0 ? "" : string.Join(", ", kept) + ".";
    }

    private WhisperProcessor BuildProcessor(string prompt)
    {
        var builder = _factory.CreateBuilder()
            .WithThreads(_threads)
            // Chaque segment est indépendant : pas de texte précédent en guise de contexte,
            // sinon une hallucination se propage aux phrases suivantes. Le prompt, lui, est
            // redonné à chaque phrase.
            .WithNoContext()
            .WithSingleSegment();
        if (prompt.Length > 0)
            builder = builder.WithPrompt(prompt);
        builder = _allowedLanguages.Length == 1
            ? builder.WithLanguage(_allowedLanguages[0])
            : builder.WithLanguageDetection();
        return builder.Build();
    }

    // Le prompt ne se change qu'en recréant le processeur : seulement quand la liste a changé.
    private void ApplyVocabulary()
    {
        string wanted = _wantedPrompt;
        if (wanted == _prompt)
            return;
        _processor.Dispose();
        _processor = BuildProcessor(wanted);
        _prompt = wanted;
        _promptWords = Words(wanted).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        // Du silence suffit à faire passer les deux étapes (détection de langue + transcription).
        var silence = new SpeechSegment(new float[MinSamples], 0, 0, 0, 0, false);
        await TranscribeAsync(silence, cancellationToken);
    }

    public async Task<TranscriptionResult?> TranscribeAsync(SpeechSegment segment, CancellationToken cancellationToken)
    {
        // Le processeur garde la langue choisie entre deux appels : une phrase à la fois.
        await _busy.WaitAsync(cancellationToken);
        try
        {
            return await TranscribeAloneAsync(segment, cancellationToken);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<TranscriptionResult?> TranscribeAloneAsync(SpeechSegment segment, CancellationToken cancellationToken)
    {
        ApplyVocabulary();
        var stopwatch = Stopwatch.StartNew();
        float[] samples = segment.Samples;
        if (samples.Length < MinSamples)
            Array.Resize(ref samples, MinSamples);

        string language;
        float languageProbability;
        if (_allowedLanguages.Length == 1)
        {
            language = _allowedLanguages[0];
            languageProbability = 1f;
        }
        else
        {
            // Meilleure langue parmi les autorisées uniquement, puis transcription dans celle-ci.
            var (detected, probability) = _processor.DetectLanguageWithProbability(samples, _allowedLanguages);
            language = detected ?? _allowedLanguages[0];
            languageProbability = probability;
            _processor.ChangeLanguage(language);
        }
        var detectionTime = stopwatch.Elapsed;

        var text = new StringBuilder();
        await foreach (var part in _processor.ProcessAsync(samples, cancellationToken))
            text.Append(part.Text);

        string result = text.ToString().Trim();
        if (result.Length == 0 || IsPromptEcho(result))
            return null;
        return new TranscriptionResult(result, language, languageProbability, segment, stopwatch.Elapsed, detectionTime);
    }

    // Sur du bruit, Whisper recrache parfois son prompt (« Alice, Bob, AWP, Mirage. ») : une
    // suite d'au moins trois mots tous tirés de la liste n'est pas une vraie phrase. Un pseudo
    // crié seul (« Alice ! ») passe toujours.
    private bool IsPromptEcho(string text)
    {
        var words = Words(text).ToList();
        return words.Count >= 3 && words.All(_promptWords.Contains);
    }

    private static IEnumerable<string> Words(string text) =>
        text.Split((char[])[' ', ',', '.', '!', '?', ';', ':', '"', '…'], StringSplitOptions.RemoveEmptyEntries);

    // Le CUDA Toolkit 13 range ses DLL (cudart, cuBLAS) dans bin\x64. Un processus lancé avant
    // l'installation (VS Code, terminal, Explorateur) garde l'ancien PATH et retomberait sans
    // rien dire sur Vulkan : on relit CUDA_PATH dans le registre système et on l'ajoute nous-mêmes.
    private static void AddCudaToPath()
    {
        string? cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH", EnvironmentVariableTarget.Machine);
        if (cudaPath is null) return;
        string bin = Path.Combine(cudaPath, "bin", "x64");
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (Directory.Exists(bin) && !path.Contains(bin, StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", bin + ";" + path);
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
        _busy.Dispose();
    }
}
