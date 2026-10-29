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
/// traités un par un.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    // whisper.cpp refuse ou transcrit mal en dessous d'une seconde : on complète par du silence.
    private const int MinSamples = SileroVad.SampleRate * 11 / 10;

    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly string[] _allowedLanguages;

    public string Description { get; }

    public WhisperTranscriber(WhisperOptions options)
    {
        _allowedLanguages = options.AllowedLanguages.ToArray();
        AddCudaToPath();
        _factory = WhisperFactory.FromPath(options.ModelPath, new WhisperFactoryOptions
        {
            UseGpu = options.UseGpu,
            // Attention « flash » : moins de VRAM pour les tampons de calcul, et plus rapide.
            UseFlashAttention = true,
        });

        var builder = _factory.CreateBuilder()
            .WithThreads(options.Threads)
            // Chaque segment est indépendant : pas de texte précédent en guise de contexte,
            // sinon une hallucination se propage aux phrases suivantes.
            .WithNoContext()
            .WithSingleSegment();
        builder = _allowedLanguages.Length == 1
            ? builder.WithLanguage(_allowedLanguages[0])
            : builder.WithLanguageDetection();
        _processor = builder.Build();

        Description = $"Whisper {Path.GetFileNameWithoutExtension(options.ModelPath)}, {RuntimeOptions.LoadedLibrary?.ToString() ?? "?"}";
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        // Du silence suffit à faire passer les deux étapes (détection de langue + transcription).
        var silence = new SpeechSegment(new float[MinSamples], 0, 0, 0, 0, false);
        await TranscribeAsync(silence, cancellationToken);
    }

    public async Task<TranscriptionResult?> TranscribeAsync(SpeechSegment segment, CancellationToken cancellationToken)
    {
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
        if (result.Length == 0)
            return null;
        return new TranscriptionResult(result, language, languageProbability, segment, stopwatch.Elapsed, detectionTime);
    }

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
    }
}
