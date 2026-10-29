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
/// Ce que l'overlay affiche : qui a parlé (vide si inconnu), le texte principal, l'original
/// s'il a été traduit, et l'appli d'où vient la voix si ce n'est pas Discord.
/// </summary>
internal sealed record DisplayLine(
    TranscriptionResult Transcription,
    IReadOnlyList<VoiceMember> Speakers,
    string Text,
    string? Original,
    string? OtherApp);

/// <summary>
/// Capture (Discord et d'autres applis) → VAD → Whisper → traduction. Tout tourne hors du
/// thread de l'interface ; les événements arrivent sur des threads de fond (à renvoyer vers le Dispatcher).
/// </summary>
internal sealed class TranslatorEngine : IAsyncDisposable
{
    internal static readonly string ModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordVoiceTranslator", "models");

    private sealed record TranslationConfig(ITranslator? Translator, TargetLanguage Target, string? DisabledReason);

    /// <summary>
    /// Une appli captée : sa propre détection de parole (Silero garde un état par flux), mais
    /// un seul Whisper partagé, qui traite les phrases l'une après l'autre.
    /// </summary>
    private sealed class AudioChannel(string name, bool isDiscord, AppAudioSource source, SileroVad vad, TranscriptionPipeline pipeline)
    {
        public string Name => name;
        public bool IsDiscord => isDiscord;
        public AppAudioSource Source => source;
        public TranscriptionPipeline Pipeline => pipeline;
        public volatile bool Speaking;
        public volatile string Status = "";

        public void Start()
        {
            pipeline.Start();
            source.Start();
        }

        public async Task DisposeAsync()
        {
            source.Dispose();
            await pipeline.DisposeAsync();
            vad.Dispose();
        }
    }

    private readonly TranscriptionSettings _transcription;
    private readonly Queue<double> _recentLatencies = new();
    private readonly Channel<(TranscriptionResult Result, AudioChannel Channel)> _toTranslate =
        Channel.CreateUnbounded<(TranscriptionResult, AudioChannel)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private string _vadPath = "";
    private WhisperTranscriber? _transcriber;
    private (bool Discord, IReadOnlyList<CapturedApp> Apps) _audioConfig = (true, []);
    private IReadOnlyList<AudioChannel> _channels = [];
    private Task? _translationTask;
    private TranslationConfig _translation = new(null, TargetLanguage.All[0], null);
    private DiscordVoiceService? _voice;
    private string _engineStatus = "", _translationStatus = "", _speakersStatus = "";
    private TranslatorUsage? _usage;
    private double? _lastTranslationMs;
    private int _linesSinceUsage;
    private int _translating;
    private volatile bool _paused;

    public event Action<string>? StatusChanged;
    public event Action<bool>? BusyChanged;
    public event Action<DisplayLine>? LineReady;

    /// <summary>Dernier état publié (Whisper, traduction, pseudos, capture), une ligne chacun.</summary>
    public string CurrentStatus { get; private set; } = "";

    public bool Paused => _paused;

    public TranslatorEngine(TranscriptionSettings transcription) => _transcription = transcription;

    public async Task StartAsync()
    {
        string modelPath = await DownloadWhisperModelAsync(WhisperModel.Find(_transcription.Model).File);
        SetEngineStatus(T("Loading Whisper...", "Chargement de Whisper..."));
        await Task.Run(async () =>
        {
            // Silero (2 Mo) est livré avec l'appli ; à défaut (version de développement), dans le dossier des modèles.
            _vadPath = Path.Combine(AppContext.BaseDirectory, "models", "silero_vad.onnx");
            if (!File.Exists(_vadPath))
                _vadPath = Path.Combine(ModelsDir, "silero_vad.onnx");
            try
            {
                var transcriber = new WhisperTranscriber(new WhisperOptions
                {
                    ModelPath = modelPath,
                    UseGpu = _transcription.UseGpu,
                    AllowedLanguages = _transcription.AllowedLanguages.Count > 0 ? _transcription.AllowedLanguages : ["en"],
                });
                await transcriber.WarmUpAsync(CancellationToken.None);
                // Prêt seulement une fois le premier passage réussi : ConfigureAudio s'y fie.
                _transcriber = transcriber;
            }
            catch (Exception ex)
            {
                // Le plus souvent un modèle trop gros pour la mémoire vidéo libre (jeu gourmand à côté).
                throw new InvalidOperationException(
                    T($"the Whisper model {Path.GetFileName(modelPath)} could not be loaded ({ex.Message}). Pick \"Fast\" in Settings > Transcription.",
                      $"le modèle Whisper {Path.GetFileName(modelPath)} n'a pas pu être chargé ({ex.Message}). Choisis « Rapide » dans Paramètres > Transcription."), ex);
            }
        });

        _translationTask = Task.Run(TranslationLoop);
        RebuildChannels();
        UpdateEngineStatus();
    }

    /// <summary>
    /// Choisit les applis à sous-titrer, à chaud. Avant la fin du chargement de Whisper, le
    /// choix est seulement retenu et appliqué dès qu'il est prêt.
    /// </summary>
    public void ConfigureAudio(bool captureDiscord, IReadOnlyList<CapturedApp> apps)
    {
        _audioConfig = (captureDiscord, apps.ToList());
        if (_transcriber is not null && !_cts.IsCancellationRequested)
            RebuildChannels();
    }

    /// <summary>
    /// En pause, plus rien n'est capté : ni Whisper ni DeepL ne travaillent (aucun caractère
    /// consommé), mais l'appli reste ouverte et Whisper chargé pour reprendre aussitôt.
    /// </summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (_transcriber is not null && !_cts.IsCancellationRequested)
            RebuildChannels();
        else
            PublishStatus();
    }

    private void RebuildChannels()
    {
        var (discord, apps) = _audioConfig;
        var channels = new List<AudioChannel>();
        if (!_paused)
        {
            if (discord)
                channels.Add(CreateChannel("Discord", isDiscord: true, AppAudioSource.Discord()));
            foreach (var app in apps)
                channels.Add(CreateChannel(app.Name, isDiscord: false, new AppAudioSource(app.Name, [app.ExeName])));
        }

        var previous = _channels;
        Volatile.Write(ref _channels, channels);
        foreach (var channel in channels)
            channel.Start();
        _ = Task.Run(async () =>
        {
            foreach (var channel in previous)
                await channel.DisposeAsync();
        });
        PublishStatus();
        RaiseBusy();
    }

    private AudioChannel CreateChannel(string name, bool isDiscord, AppAudioSource source)
    {
        var vad = new SileroVad(_vadPath);
        var segmenter = new SpeechSegmenter(vad, new VadOptions { MinSilenceMs = _transcription.EndOfSentenceSilenceMs });
        var pipeline = new TranscriptionPipeline(segmenter, _transcriber!);
        var channel = new AudioChannel(name, isDiscord, source, vad, pipeline);

        pipeline.SpeechStarted += _ => { channel.Speaking = true; RaiseBusy(); };
        pipeline.SpeechDiscarded += () => { channel.Speaking = false; RaiseBusy(); };
        pipeline.SegmentQueued += segment => { if (!segment.CutAtMaxLength) channel.Speaking = false; RaiseBusy(); };
        pipeline.SegmentProcessed += RaiseBusy;
        pipeline.Transcribed += result =>
        {
            Interlocked.Increment(ref _translating);
            _toTranslate.Writer.TryWrite((result, channel));
        };
        pipeline.Error += ex => SetEngineStatus(T("Transcription error: ", "Erreur de transcription : ") + ex.Message);
        source.StatusChanged += status => { channel.Status = status; PublishStatus(); };
        source.Data16kMono += pipeline.PushAudio;
        return channel;
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
            await foreach (var (result, channel) in _toTranslate.Reader.ReadAllAsync(_cts.Token))
            {
                // Phrase finie juste avant la pause : on la jette plutôt que de la traduire.
                if (_paused)
                {
                    Interlocked.Decrement(ref _translating);
                    RaiseBusy();
                    continue;
                }
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
                // Les pseudos ne valent que pour Discord : la voix d'un jeu n'y figure pas.
                var speakers = channel.IsDiscord
                    ? Volatile.Read(ref _voice)?.IdentifySpeakers(result.Segment.SpeechStartQpc, result.Segment.SpeechEndQpc) ?? []
                    : [];
                RecordLatency(QpcClock.ToMilliseconds(QpcClock.Now100ns - result.Segment.SpeechEndQpc));
                LineReady?.Invoke(new DisplayLine(result, speakers, text, original, channel.IsDiscord ? null : channel.Name));
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
        BusyChanged?.Invoke(Volatile.Read(ref _channels).Any(c => c.Speaking || c.Pipeline.PendingSegments > 0)
            || Volatile.Read(ref _translating) > 0);

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
        var channels = Volatile.Read(ref _channels);
        string capture = _paused ? T("PAUSED: nothing is listened to or translated (Ctrl+Alt+F11 to resume)",
                                     "EN PAUSE : rien n'est écouté ni traduit (Ctrl+Alt+F11 pour reprendre)")
            : channels.Count > 0 ? string.Join("\n", channels.Select(c => c.Status).Where(s => s.Length > 0))
            : _transcriber is null ? "" : T("Audio: no app selected (Settings > Audio)", "Audio : aucune appli choisie (Paramètres > Audio)");
        CurrentStatus = string.Join("\n", new[] { _engineStatus, _translationStatus, _speakersStatus, capture }.Where(s => s.Length > 0));
        StatusChanged?.Invoke(CurrentStatus);
    }

    public async ValueTask DisposeAsync()
    {
        if (_voice is not null) await _voice.DisposeAsync();
        foreach (var channel in Volatile.Read(ref _channels))
            await channel.DisposeAsync();
        _toTranslate.Writer.TryComplete();
        _cts.Cancel();
        if (_translationTask is not null) await _translationTask;
        _translation.Translator?.Dispose();
        _transcriber?.Dispose();
        _cts.Dispose();
    }
}
