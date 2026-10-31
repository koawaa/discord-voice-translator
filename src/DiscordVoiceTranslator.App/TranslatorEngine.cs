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
/// <param name="Source">L'appli captée : sa phrase finale remplace son dernier aperçu.</param>
/// <param name="IsDraft">Aperçu d'une phrase encore en cours, remplacé ensuite.</param>
internal sealed record DisplayLine(
    TranscriptionResult Transcription,
    IReadOnlyList<VoiceMember> Speakers,
    string Text,
    string? Original,
    string? OtherApp,
    string Source,
    bool IsDraft = false);

/// <summary>
/// Capture (Discord et d'autres applis) → VAD → Whisper → traduction. Tout tourne hors du
/// thread de l'interface ; les événements arrivent sur des threads de fond (à renvoyer vers le Dispatcher).
/// </summary>
internal sealed class TranslatorEngine : IAsyncDisposable
{
    internal static readonly string ModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordVoiceTranslator", "models");

    private sealed record TranslationConfig(ITranslator? Translator, TargetLanguage Target, bool Informal, string? DisabledReason);

    // Au-delà, la phrase précédente parle sans doute d'autre chose : elle égarerait la traduction.
    private static readonly long ContextMaxAge = TimeSpan.FromSeconds(20).Ticks;

    // Assez souvent pour suivre un jeu lancé ou quelqu'un qui rejoint le salon, sans peser.
    private static readonly TimeSpan VocabularyRefresh = TimeSpan.FromSeconds(10);

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

        // Dernière phrase reconnue (langue d'origine), donnée comme contexte à la suivante.
        // Écrite seulement par la boucle de traduction.
        public volatile string? LastText;
        public long LastTextEndQpc;

        // Aperçus : seul le plus récent attend, traduit par un seul travail à la fois. Un aperçu
        // dont la phrase finale est déjà partie (numéro ≤ LastFinalUtterance) est jeté.
        public readonly object DraftLock = new();
        public TranscriptionResult? PendingDraft;
        public int DraftWorking;
        public long LastFinalUtterance;

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
    private Task? _translationTask, _vocabularyTask;
    private TranslationConfig _translation = new(null, TargetLanguage.All[0], false, null);
    private DiscordVoiceService? _voice;
    private readonly List<GameVocabulary> _gamePacks = GameVocabulary.LoadAll();
    private IReadOnlyCollection<string> _keepAsIs = [];
    private HashSet<string> _hiddenSpeakers = [];
    private string _vocabularyKey = "";
    private string _engineStatus = "", _translationStatus = "", _speakersStatus = "", _vocabularyStatus = "";
    private TranslatorUsage? _usage;
    private double? _lastTranslationMs;
    private int _linesSinceUsage;
    private int _translating;
    private volatile bool _paused;

    public event Action<string>? StatusChanged;
    public event Action<bool>? BusyChanged;
    public event Action<DisplayLine>? LineReady;

    /// <summary>La prise de parole de cette source n'a rien donné : son aperçu doit disparaître.</summary>
    public event Action<string>? DraftCancelled;

    /// <summary>Dernier état publié (Whisper, traduction, pseudos, capture), une ligne chacun.</summary>
    public string CurrentStatus { get; private set; } = "";

    public bool Paused => _paused;

    public TranslatorEngine(TranscriptionSettings transcription) => _transcription = transcription;

    /// <summary>IDs Discord des personnes dont les phrases ne sont ni traduites ni affichées.</summary>
    public void SetHiddenSpeakers(IEnumerable<string> ids) =>
        Volatile.Write(ref _hiddenSpeakers, ids.ToHashSet());

    /// <summary>Langue des dernières phrases entendues dans une autre langue que la tienne : celle où répondre.</summary>
    public TargetLanguage? LastHeardLanguage { get; private set; }

    /// <summary>
    /// Traduit ce que tu veux répondre, avec le même traducteur, le même ton et les mêmes
    /// termes protégés que les sous-titres. La langue de départ est détectée par le fournisseur.
    /// </summary>
    /// <exception cref="TranslationException">Traduction coupée, pas de clé, quota épuisé...</exception>
    public Task<string> TranslateReplyAsync(string text, TargetLanguage target, CancellationToken cancellationToken)
    {
        var config = Volatile.Read(ref _translation);
        if (config.Translator is null)
            throw new TranslationException(config.DisabledReason ?? T("Translation off", "Traduction désactivée"));
        return config.Translator.TranslateAsync(text, "", target.Code, new TranslationHints(null, config.Informal, Volatile.Read(ref _keepAsIs)), cancellationToken);
    }

    /// <summary>Sous-titres en direct (aperçus pendant que la personne parle), à chaud.</summary>
    public void SetLiveDrafts(bool live)
    {
        foreach (var channel in Volatile.Read(ref _channels))
            channel.Pipeline.LiveDrafts = live;
    }

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
        _vocabularyTask = Task.Run(VocabularyLoop);
        RebuildChannels();
        UpdateEngineStatus();
    }

    /// <summary>
    /// Toutes les 10 s : pseudos du salon vocal et vocabulaire du jeu en cours, donnés à
    /// Whisper (orthographe) et protégés de la traduction. Coupé dans les paramètres : rien.
    /// </summary>
    private async Task VocabularyLoop()
    {
        try
        {
            while (true)
            {
                RefreshVocabulary();
                await Task.Delay(VocabularyRefresh, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Appelé par la boucle et après les paramètres : un seul à la fois.
    private void RefreshVocabulary()
    {
        lock (_gamePacks)
            RefreshVocabularyLocked();
    }

    private void RefreshVocabularyLocked()
    {
        IReadOnlyList<string> names = [];
        List<GameVocabulary> games = [];
        if (_transcription.AutoVocabulary)
        {
            names = Volatile.Read(ref _voice)?.ChannelMemberNames ?? [];
            if (_gamePacks.Count > 0)
                games = GameVocabulary.Running(_gamePacks);
        }

        // Les pseudos d'abord : peu nombreux, et ce sont eux que Whisper écorche le plus.
        var terms = names.Concat(games.SelectMany(g => g.Terms)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string key = string.Join("\n", terms);
        if (key == _vocabularyKey)
            return;
        _vocabularyKey = key;
        _transcriber?.SetVocabulary(terms);
        Volatile.Write(ref _keepAsIs, terms);

        var parts = games.Select(g => g.Game).ToList();
        if (names.Count > 0)
            parts.Add(T($"{names.Count} name(s)", $"{names.Count} pseudo(s)"));
        _vocabularyStatus = !_transcription.AutoVocabulary ? ""
            : parts.Count == 0 ? T("Vocabulary: no game list or names yet", "Vocabulaire : ni liste de jeu ni pseudo pour l'instant")
            : T("Vocabulary: ", "Vocabulaire : ") + string.Join(" + ", parts);
        PublishStatus();
    }

    /// <summary>Après un changement dans les paramètres : appliqué tout de suite plutôt qu'au prochain tour.</summary>
    public void RefreshVocabularyNow()
    {
        if (_transcriber is not null)
            _ = Task.Run(RefreshVocabulary);
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
        var pipeline = new TranscriptionPipeline(segmenter, _transcriber!) { LiveDrafts = _transcription.LiveSubtitles };
        var channel = new AudioChannel(name, isDiscord, source, vad, pipeline);
        pipeline.DraftTranscribed += result => QueueDraft(channel, result);
        pipeline.NothingRecognized += _ => DraftCancelled?.Invoke(channel.Name);

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
    public void ConfigureTranslation(ITranslator? translator, TargetLanguage target, bool informal, string? disabledReason = null)
    {
        var previous = Interlocked.Exchange(ref _translation, new TranslationConfig(translator, target, informal, disabledReason));
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
                // Dès maintenant, avant la traduction : un aperçu de cette phrase encore en route ne
                // doit plus s'afficher, il arriverait après la version finale.
                lock (channel.DraftLock)
                    channel.LastFinalUtterance = Math.Max(channel.LastFinalUtterance, result.Segment.UtteranceId);

                // Personne masquée : jetée avant la traduction, elle ne coûte aucun caractère.
                if (IsHidden(channel, result))
                {
                    Interlocked.Decrement(ref _translating);
                    DraftCancelled?.Invoke(channel.Name);
                    RaiseBusy();
                    continue;
                }

                var config = Volatile.Read(ref _translation);
                if (!config.Target.Matches(result.Language))
                    LastHeardLanguage = TargetLanguage.All.FirstOrDefault(l => l.Matches(result.Language)) ?? LastHeardLanguage;
                string text = result.Text;
                string? original = null;
                // Contexte : la phrase d'avant sur la même source (une question, puis sa réponse).
                string? context = channel.LastText is not null && result.Segment.SpeechStartQpc - channel.LastTextEndQpc < ContextMaxAge
                    ? channel.LastText
                    : null;
                channel.LastText = result.Text;
                channel.LastTextEndQpc = result.Segment.SpeechEndQpc;
                if (config.Translator is not null && !config.Target.Matches(result.Language))
                {
                    var stopwatch = Stopwatch.StartNew();
                    string nameBefore = config.Translator.Name;
                    try
                    {
                        var hints = new TranslationHints(context, config.Informal, Volatile.Read(ref _keepAsIs));
                        text = await config.Translator.TranslateAsync(result.Text, result.Language, config.Target.Code, hints, _cts.Token);
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

                RecordLatency(QpcClock.ToMilliseconds(QpcClock.Now100ns - result.Segment.SpeechEndQpc));
                LineReady?.Invoke(new DisplayLine(result, Speakers(channel, result), text, original, channel.IsDiscord ? null : channel.Name, channel.Name));
                RaiseBusy();

                if (config.Translator is not null && ++_linesSinceUsage >= 10)
                    _ = RefreshUsageAsync(config.Translator);
            }
        }
        catch (OperationCanceledException) { }
    }

    // La parole seule (sans le pré-buffer), pour ne pas compter quelqu'un qui finissait juste avant.
    // Les pseudos ne valent que pour Discord : la voix d'un jeu n'y figure pas.
    private IReadOnlyList<VoiceMember> Speakers(AudioChannel channel, TranscriptionResult result) =>
        channel.IsDiscord
            ? Volatile.Read(ref _voice)?.IdentifySpeakers(result.Segment.SpeechStartQpc, result.Segment.SpeechEndQpc) ?? []
            : [];

    private void QueueDraft(AudioChannel channel, TranscriptionResult draft)
    {
        Volatile.Write(ref channel.PendingDraft, draft);
        if (Interlocked.CompareExchange(ref channel.DraftWorking, 1, 0) == 0)
            _ = Task.Run(() => TranslateDraftsAsync(channel));
    }

    /// <summary>
    /// Traduit le dernier aperçu d'une source, à côté de la boucle des phrases finies pour ne
    /// jamais les retarder. Chaque aperçu traduit consomme des caractères du quota.
    /// </summary>
    private async Task TranslateDraftsAsync(AudioChannel channel)
    {
        while (true)
        {
            var draft = Interlocked.Exchange(ref channel.PendingDraft, null);
            if (draft is null)
            {
                Volatile.Write(ref channel.DraftWorking, 0);
                // Un aperçu arrivé entre-temps, sans relancer de travail : on le prend.
                if (Volatile.Read(ref channel.PendingDraft) is null || Interlocked.CompareExchange(ref channel.DraftWorking, 1, 0) != 0)
                    return;
                continue;
            }
            if (IsStale(channel, draft) || IsHidden(channel, draft))
                continue;

            var config = Volatile.Read(ref _translation);
            string text = draft.Text;
            string? original = null;
            if (config.Translator is not null && !config.Target.Matches(draft.Language))
            {
                try
                {
                    var hints = new TranslationHints(channel.LastText, config.Informal, Volatile.Read(ref _keepAsIs));
                    text = await config.Translator.TranslateAsync(draft.Text, draft.Language, config.Target.Code, hints, _cts.Token);
                    original = draft.Text;
                }
                catch (TranslationException)
                {
                    // Un aperçu non traduit vaut mieux que rien ; l'erreur s'affichera avec la phrase finale.
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            lock (channel.DraftLock)
            {
                if (!IsStale(channel, draft))
                    LineReady?.Invoke(new DisplayLine(draft, Speakers(channel, draft), text, original,
                        channel.IsDiscord ? null : channel.Name, channel.Name, IsDraft: true));
            }
        }
    }

    // Seulement si toutes les voix de la phrase sont masquées : « Mia / Jonas » reste si Jonas seul l'est.
    private bool IsHidden(AudioChannel channel, TranscriptionResult result)
    {
        var hidden = Volatile.Read(ref _hiddenSpeakers);
        if (hidden.Count == 0)
            return false;
        var speakers = Speakers(channel, result);
        return speakers.Count > 0 && speakers.All(s => hidden.Contains(s.Id));
    }

    private bool IsStale(AudioChannel channel, TranscriptionResult draft) =>
        _paused || draft.Segment.UtteranceId <= Volatile.Read(ref channel.LastFinalUtterance);

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
        CurrentStatus = string.Join("\n", new[] { _engineStatus, _translationStatus, _speakersStatus, _vocabularyStatus, capture }.Where(s => s.Length > 0));
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
        if (_vocabularyTask is not null) await _vocabularyTask;
        _translation.Translator?.Dispose();
        _transcriber?.Dispose();
        _cts.Dispose();
    }
}
