using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Channels;
using DiscordVoiceTranslator.Core.Audio;
using DiscordVoiceTranslator.Core.Diagnostics;
using DiscordVoiceTranslator.Core.Discord;
using DiscordVoiceTranslator.Core.Models;
using DiscordVoiceTranslator.Core.Pipeline;
using DiscordVoiceTranslator.Core.Timing;
using DiscordVoiceTranslator.Core.Transcription;
using DiscordVoiceTranslator.Core.Translation;
using DiscordVoiceTranslator.Core.Vad;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.App;

/// <summary>
/// Ce que l'overlay affiche : qui a parlé (vide si inconnu), le texte principal, et
/// l'original s'il a été traduit.
/// </summary>
internal sealed record DisplayLine(
    TranscriptionResult Transcription,
    IReadOnlyList<VoiceMember> Speakers,
    string Text,
    string? Original);

/// <summary>
/// Capture Discord → VAD → Whisper → traduction. Tout tourne hors du thread de l'interface ;
/// les événements arrivent sur des threads de fond (à renvoyer vers le Dispatcher).
/// </summary>
internal sealed class TranslatorEngine : IAsyncDisposable
{
    internal static readonly string ModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordVoiceTranslator", "models");

    private sealed record TranslationConfig(ITranslator? Translator, TargetLanguage Target, string? DisabledReason);

    private readonly TranscriptionSettings _transcription;
    private readonly Queue<double> _recentLatencies = new();
    private readonly Channel<TranscriptionResult> _toTranslate =
        Channel.CreateUnbounded<TranscriptionResult>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private DiscordAudioSource? _source;
    private SileroVad? _vad;
    private WhisperTranscriber? _transcriber;
    private TranscriptionPipeline? _pipeline;
    private Task? _translationTask;
    private TranslationConfig _translation = new(null, TargetLanguage.All[0], null);
    private DiscordVoiceService? _voice;
    private string _engineStatus = "", _translationStatus = "", _speakersStatus = "", _captureStatus = "";
    private TranslatorUsage? _usage;
    private double? _lastTranslationMs;
    private int _linesSinceUsage;
    private volatile bool _speaking;
    private int _translating;

    public event Action<string>? StatusChanged;
    public event Action<bool>? BusyChanged;
    public event Action<DisplayLine>? LineReady;

    /// <summary>Dernier état publié (Whisper, traduction, pseudos, capture), une ligne chacun.</summary>
    public string CurrentStatus { get; private set; } = "";

    public TranslatorEngine(TranscriptionSettings transcription) => _transcription = transcription;

    public async Task StartAsync()
    {
        string modelPath = await DownloadWhisperModelAsync(WhisperModel.Find(_transcription.Model).File);
        SetEngineStatus(T("Loading Whisper...", "Chargement de Whisper..."));
        await Task.Run(async () =>
        {
            // Silero (2 Mo) est livré avec l'appli ; à défaut (version de développement), dans le dossier des modèles.
            string vadPath = Path.Combine(AppContext.BaseDirectory, "models", "silero_vad.onnx");
            if (!File.Exists(vadPath))
                vadPath = Path.Combine(ModelsDir, "silero_vad.onnx");
            _vad = new SileroVad(vadPath);
            try
            {
                _transcriber = new WhisperTranscriber(new WhisperOptions
                {
                    ModelPath = modelPath,
                    UseGpu = _transcription.UseGpu,
                    AllowedLanguages = _transcription.AllowedLanguages.Count > 0 ? _transcription.AllowedLanguages : ["en"],
                });
                await _transcriber.WarmUpAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Le plus souvent un modèle trop gros pour la mémoire vidéo libre (jeu gourmand à côté).
                throw new InvalidOperationException(
                    T($"the Whisper model {Path.GetFileName(modelPath)} could not be loaded ({ex.Message}). Pick \"Fast\" in Settings > Transcription.",
                      $"le modèle Whisper {Path.GetFileName(modelPath)} n'a pas pu être chargé ({ex.Message}). Choisis « Rapide » dans Paramètres > Transcription."), ex);
            }
        });

        var vadOptions = new VadOptions { MinSilenceMs = _transcription.EndOfSentenceSilenceMs };
        _pipeline = new TranscriptionPipeline(new SpeechSegmenter(_vad!, vadOptions), _transcriber!);
        _pipeline.SpeechStarted += _ => { _speaking = true; RaiseBusy(); };
        _pipeline.SpeechDiscarded += () => { _speaking = false; RaiseBusy(); };
        _pipeline.SegmentQueued += segment => { if (!segment.CutAtMaxLength) _speaking = false; RaiseBusy(); };
        _pipeline.SegmentProcessed += RaiseBusy;
        _pipeline.Transcribed += result =>
        {
            Interlocked.Increment(ref _translating);
            _toTranslate.Writer.TryWrite(result);
        };
        _pipeline.Error += ex => SetEngineStatus(T("Transcription error: ", "Erreur de transcription : ") + ex.Message);
        _translationTask = Task.Run(TranslationLoop);
        _pipeline.Start();

        _source = new DiscordAudioSource();
        _source.StatusChanged += status => { _captureStatus = status; PublishStatus(); };
        _source.Data16kMono += _pipeline.PushAudio;
        _source.Start();

        UpdateEngineStatus();
    }

    /// <summary>
    /// Au premier lancement (ou après avoir choisi un autre modèle), le modèle Whisper n'est pas
    /// encore là : on le télécharge en affichant la progression, et on réessaie tout seul si la connexion coupe.
    /// </summary>
    private async Task<string> DownloadWhisperModelAsync(ModelFile model)
    {
        double mb = model.Size / 1024.0 / 1024;
        while (true)
        {
            try
            {
                return await ModelDownloader.EnsureAsync(model, ModelsDir, fraction =>
                    SetEngineStatus(T($"Downloading the speech model (only once): {fraction:P0} of {mb:0} MB",
                                      $"Téléchargement du modèle de reconnaissance (une seule fois) : {fraction:P0} de {mb:0} Mo")),
                    _cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                SetEngineStatus(T($"Model download failed ({ex.Message}). Retrying in 10 s...",
                                  $"Échec du téléchargement du modèle ({ex.Message}). Nouvel essai dans 10 s..."));
                await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
            }
        }
    }

    /// <summary>
    /// Change de traducteur ou de langue cible à chaud. translator = null : sans traduction,
    /// disabledReason dit pourquoi dans la ligne d'état.
    /// </summary>
    public void ConfigureTranslation(ITranslator? translator, TargetLanguage target, string? disabledReason = null)
    {
        var previous = Interlocked.Exchange(ref _translation, new TranslationConfig(translator, target, disabledReason));
        // Une traduction peut encore être en cours avec l'ancien traducteur : on le libère plus tard.
        if (previous.Translator is not null && previous.Translator != translator)
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => previous.Translator.Dispose());

        _usage = null;
        _lastTranslationMs = null;
        UpdateTranslationStatus();
        if (translator is not null)
            _ = RefreshUsageAsync(translator);
    }

    /// <summary>
    /// Démarre (ou redémarre) le suivi des pseudos via le RPC de Discord ; null pour l'arrêter.
    /// </summary>
    public void ConfigureDiscord(DiscordAppCredentials? app, int marginMs)
    {
        var previous = Interlocked.Exchange(ref _voice, null);
        if (previous is not null)
            _ = previous.DisposeAsync().AsTask();

        if (app is null)
        {
            _speakersStatus = T("Names: Discord app not set up (Settings > Discord)",
                                "Pseudos : application Discord non configurée (Paramètres > Discord)");
        }
        else
        {
            var voice = new DiscordVoiceService(app) { Margin = TimeSpan.FromMilliseconds(marginMs) };
            voice.StatusChanged += status =>
            {
                // Un ancien service qui s'arrête ne doit pas écraser l'état du nouveau.
                if (Volatile.Read(ref _voice) != voice) return;
                _speakersStatus = status;
                PublishStatus();
            };
            _speakersStatus = T("Names: connecting to Discord...", "Pseudos : connexion à Discord...");
            Volatile.Write(ref _voice, voice);
            voice.Start();
        }
        PublishStatus();
    }

    /// <summary>Marge d'alignement des pseudos, modifiable sans reconnecter le RPC.</summary>
    public void SetSpeakerMargin(int marginMs)
    {
        if (Volatile.Read(ref _voice) is { } voice)
            voice.Margin = TimeSpan.FromMilliseconds(marginMs);
    }

    private async Task TranslationLoop()
    {
        try
        {
            // Une phrase à la fois, dans l'ordre : les lignes ne peuvent pas s'afficher dans le désordre.
            await foreach (var result in _toTranslate.Reader.ReadAllAsync(_cts.Token))
            {
                var config = Volatile.Read(ref _translation);
                string text = result.Text;
                string? original = null;
                if (config.Translator is not null && !config.Target.Matches(result.Language))
                {
                    var stopwatch = Stopwatch.StartNew();
                    string nameBefore = config.Translator.Name;
                    try
                    {
                        text = await config.Translator.TranslateAsync(result.Text, result.Language, config.Target.Code, _cts.Token);
                        original = result.Text;
                        _lastTranslationMs = stopwatch.Elapsed.TotalMilliseconds;
                        UpdateTranslationStatus();
                    }
                    catch (TranslationException ex)
                    {
                        // On affiche quand même le texte d'origine plutôt que de perdre la phrase.
                        UpdateTranslationStatus(ex.Message);
                    }
                    // Passé à une autre clé : le quota affiché était celui de l'ancienne.
                    if (config.Translator.Name != nameBefore)
                        _ = RefreshUsageAsync(config.Translator);
                }
                Interlocked.Decrement(ref _translating);

                // La parole seule (sans le pré-buffer), pour ne pas compter quelqu'un qui finissait juste avant.
                var speakers = Volatile.Read(ref _voice)?.IdentifySpeakers(result.Segment.SpeechStartQpc, result.Segment.SpeechEndQpc) ?? [];
                RecordLatency(QpcClock.ToMilliseconds(QpcClock.Now100ns - result.Segment.SpeechEndQpc));
                LineReady?.Invoke(new DisplayLine(result, speakers, text, original));
                RaiseBusy();

                if (config.Translator is not null && ++_linesSinceUsage >= 10)
                    _ = RefreshUsageAsync(config.Translator);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshUsageAsync(ITranslator translator)
    {
        _linesSinceUsage = 0;
        try
        {
            var usage = await translator.GetUsageAsync(_cts.Token);
            if (Volatile.Read(ref _translation).Translator == translator)
            {
                _usage = usage;
                UpdateTranslationStatus();
            }
        }
        catch (TranslationException ex) { UpdateTranslationStatus(ex.Message); }
        catch (OperationCanceledException) { }
    }

    // « En cours... » : quelqu'un parle, ou une phrase attend sa transcription ou sa traduction.
    private void RaiseBusy() =>
        BusyChanged?.Invoke(_speaking || _pipeline?.PendingSegments > 0 || Volatile.Read(ref _translating) > 0);

    private void RecordLatency(double milliseconds)
    {
        lock (_recentLatencies)
        {
            _recentLatencies.Enqueue(milliseconds);
            while (_recentLatencies.Count > 20)
                _recentLatencies.Dequeue();
        }
        UpdateEngineStatus();
    }

    private void UpdateEngineStatus()
    {
        string vram = GpuMemory.DedicatedBytesForCurrentProcess() is long bytes ? $"{bytes / 1024 / 1024} MB" : "?";
        string latency;
        lock (_recentLatencies)
        {
            int count = _recentLatencies.Count;
            double seconds = count == 0 ? 0 : _recentLatencies.Average() / 1000;
            latency = count == 0
                ? T("no sentence yet", "pas encore de phrase")
                : T($"{seconds:0.0} s average over {count} sentence(s)", $"{seconds:0.0} s en moyenne sur {count} phrase(s)");
        }
        SetEngineStatus($"{_transcriber?.Description} · VRAM {vram} · {T("sentence end → display", "fin de phrase → affichage")} {latency}");
    }

    private void UpdateTranslationStatus(string? error = null)
    {
        var config = Volatile.Read(ref _translation);
        if (config.Translator is null)
        {
            _translationStatus = config.DisabledReason ?? T("Translation off", "Traduction désactivée");
        }
        else
        {
            string status = T($"Translation {config.Translator.Name} → {config.Target.DisplayName}",
                              $"Traduction {config.Translator.Name} → {config.Target.DisplayName}");
            if (_lastTranslationMs is double ms)
                status += T($" · last one {ms:0} ms", $" · dernière en {ms:0} ms");
            if (_usage is { } usage)
                status += T($" · {usage.CharactersUsed:N0} / {usage.CharacterLimit:N0} characters this month",
                            $" · {usage.CharactersUsed:N0} / {usage.CharacterLimit:N0} caractères ce mois");
            if (error is not null)
                status += T(" · ERROR: ", " · ERREUR : ") + error;
            _translationStatus = status;
        }
        PublishStatus();
    }

    private void SetEngineStatus(string status)
    {
        _engineStatus = status;
        PublishStatus();
    }

    private void PublishStatus()
    {
        CurrentStatus = string.Join("\n", new[] { _engineStatus, _translationStatus, _speakersStatus, _captureStatus }.Where(s => s.Length > 0));
        StatusChanged?.Invoke(CurrentStatus);
    }

    public async ValueTask DisposeAsync()
    {
        if (_voice is not null) await _voice.DisposeAsync();
        _source?.Dispose();
        if (_pipeline is not null) await _pipeline.DisposeAsync();
        _toTranslate.Writer.TryComplete();
        _cts.Cancel();
        if (_translationTask is not null) await _translationTask;
        _translation.Translator?.Dispose();
        _transcriber?.Dispose();
        _vad?.Dispose();
        _cts.Dispose();
    }
}
