using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscordVoiceTranslator.Core.Config;
using DiscordVoiceTranslator.Core.Discord;
using DiscordVoiceTranslator.Core.Localization;
using DiscordVoiceTranslator.Core.Translation;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.App;

public partial class App : Application
{
    private const string RestartedArg = "--restarted";
    private const string InstanceMutexName = @"Local\DiscordVoiceTranslator";
    private const string ShowRequestName = @"Local\DiscordVoiceTranslator.ShowRequest";
    private const string ShowDoneName = @"Local\DiscordVoiceTranslator.ShowDone";
    // Signalé par l'installateur avant une mise à jour ou une désinstallation.
    private const string QuitRequestName = @"Local\DiscordVoiceTranslator.Quit";

    private readonly AppSettings _settings = AppSettings.Load();
    private Mutex? _instanceMutex;
    private bool _ownsInstance;
    private OverlayWindow? _overlay;
    private TrayIcon? _tray;
    private TranslatorEngine? _engine;
    private DispatcherTimer? _demoTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UiText.French = _settings.UiLanguage == "fr";

        // Une seule instance : deux overlays se disputeraient les raccourcis et la capture.
        // Après un redémarrage demandé par les réglages, on attend que l'ancienne se ferme.
        _instanceMutex = new Mutex(false, InstanceMutexName);
        _ownsInstance = TryOwnInstance(e.Args.Contains(RestartedArg) ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        if (!_ownsInstance)
        {
            // Déjà lancée : verrouillée sans sous-titre, elle est invisible et son icône peut être
            // cachée. On lui demande de se montrer en mode édition, et on s'arrête là.
            if (AskRunningInstanceToShow())
            {
                Shutdown();
                return;
            }
            // Pas de réponse : l'autre instance est bloquée. On la ferme et on prend sa place.
            CloseOtherInstances();
            _ownsInstance = TryOwnInstance(TimeSpan.FromSeconds(10));
            if (!_ownsInstance)
            {
                MessageBox.Show(T("Another copy of Discord Voice Translator is stuck and could not be closed. Restart Windows, then try again.",
                                  "Une autre copie du traducteur vocal Discord est bloquée et n'a pas pu être fermée. Redémarre Windows, puis réessaie."),
                                T("Discord Voice Translator", "Traducteur vocal Discord"));
                Shutdown();
                return;
            }
        }

        // Le jeu passe avant nous pour le CPU.
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;

        var overlay = _overlay = new OverlayWindow(_settings.Overlay);
        overlay.SettingsRequested += OpenSettings;
        overlay.PauseToggleRequested += TogglePause;
        overlay.GeometryChanged += SaveSettings;
        overlay.Show();

        ListenForRequests(overlay);

        _tray = new TrayIcon();
        _tray.EditModeRequested += overlay.ToggleEditMode;
        _tray.VisibilityToggleRequested += overlay.ToggleVisibility;
        _tray.PauseToggleRequested += TogglePause;
        _tray.SettingsRequested += OpenSettings;
        _tray.QuitRequested += () => Shutdown();

        if (e.Args.Contains("--demo"))
        {
            StartDemo(overlay);
            return;
        }

        _engine = new TranslatorEngine(_settings.Transcription);
        _engine.StatusChanged += status => Dispatcher.BeginInvoke(() => overlay.SetStatus(status));
        _engine.BusyChanged += busy => Dispatcher.BeginInvoke(() => overlay.SetBusy(busy));
        _engine.LineReady += line => Dispatcher.BeginInvoke(() => ShowLine(overlay, line));
        ApplyTranslation(TranslationSettings.LoadDeepLKeys());
        _engine.ConfigureDiscord(DiscordAppCredentials.Load(), _settings.Discord.SpeakerMarginMs);
        _engine.ConfigureAudio(_settings.Audio.CaptureDiscord, _settings.Audio.Apps);

        // Tout premier lancement : les paramètres expliquent quoi configurer (Discord, DeepL).
        // Ouverts sans attendre, pendant que le modèle se télécharge.
        if (!AppSettings.Exists)
        {
            SaveSettings();
            _ = Dispatcher.BeginInvoke(OpenSettings);
        }

        try
        {
            await _engine.StartAsync();
        }
        catch (Exception ex)
        {
            overlay.SetStatus(T("Could not start: ", "Impossible de démarrer : ") + ex.Message);
        }
    }

    private bool TryOwnInstance(TimeSpan wait)
    {
        try
        {
            return _instanceMutex!.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            return true; // l'ancienne instance a planté : la place est libre
        }
    }

    /// <summary>
    /// Côté nouvelle instance : demande à celle déjà lancée de se montrer. Vrai si elle a
    /// répondu ; faux si elle ne répond pas (bloquée) ou est trop ancienne pour savoir répondre.
    /// </summary>
    private static bool AskRunningInstanceToShow()
    {
        using var request = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName);
        using var shown = new EventWaitHandle(false, EventResetMode.AutoReset, ShowDoneName);
        request.Set();
        return shown.WaitOne(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Côté instance lancée : se montre en mode édition quand une autre le demande, et quitte
    /// proprement (réglages enregistrés, icône retirée) quand l'installateur le demande.
    /// </summary>
    private void ListenForRequests(OverlayWindow overlay)
    {
        var request = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName);
        var shown = new EventWaitHandle(false, EventResetMode.AutoReset, ShowDoneName);
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitRequestName);
        var listener = new Thread(() =>
        {
            while (true)
            {
                if (WaitHandle.WaitAny([request, quit]) == 1)
                {
                    Dispatcher.BeginInvoke(() => Shutdown());
                    return;
                }
                // On ne répond qu'une fois l'overlay réellement affiché : si l'interface est
                // bloquée, l'autre instance le saura (pas de réponse) et prendra la relève.
                var operation = Dispatcher.BeginInvoke(overlay.ShowForEditing);
                if (operation.Wait(TimeSpan.FromSeconds(2)) == DispatcherOperationStatus.Completed)
                    shown.Set();
            }
        }) { IsBackground = true, Name = "Show requests" };
        listener.Start();
    }

    private static void CloseOtherInstances()
    {
        int self = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("DiscordVoiceTranslator").Where(p => p.Id != self))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Déjà terminée entre-temps, ou refusée : TryOwnInstance le dira.
                }
            }
        }
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings, () => _engine?.CurrentStatus ?? "");
        window.ResetPositionRequested += () => _overlay?.ResetPosition();
        if (window.ShowDialog() != true)
            return;

        SaveSettings();
        _overlay?.ApplySettings();
        if (window.TranslationChanged)
            ApplyTranslation(window.DeepLKeys);
        if (window.AudioChanged)
            _engine?.ConfigureAudio(_settings.Audio.CaptureDiscord, _settings.Audio.Apps);
        if (window.DiscordChanged)
            _engine?.ConfigureDiscord(window.Discord, _settings.Discord.SpeakerMarginMs);
        else
            _engine?.SetSpeakerMargin(_settings.Discord.SpeakerMarginMs);

        if (window.RestartNeeded)
        {
            var answer = MessageBox.Show(
                T("Some changes (interface language, transcription) need a restart. Restart now?",
                  "Certains changements (langue de l'interface, transcription) demandent un redémarrage. Redémarrer maintenant ?"),
                T("Discord Voice Translator", "Traducteur vocal Discord"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
                Restart();
        }
    }

    /// <summary>
    /// Pause : l'appli reste ouverte mais n'écoute plus rien, donc ne consomme ni caractères
    /// DeepL ni ressources pour Whisper. Pas retenue d'un lancement à l'autre.
    /// </summary>
    private void TogglePause()
    {
        if (_engine is null)
            return;
        bool paused = !_engine.Paused;
        _engine.SetPaused(paused);
        _overlay?.SetPaused(paused);
        _tray?.SetPaused(paused);
    }

    private void Restart()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, RestartedArg) { UseShellExecute = false });
        Shutdown();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _overlay?.SetStatus(T("Could not save settings: ", "Impossible d'enregistrer les réglages : ") + ex.Message);
        }
    }

    private void ApplyTranslation(IReadOnlyList<string> deepLKeys)
    {
        if (_engine is null)
            return;
        var target = TargetLanguage.Find(_settings.Translation.TargetLanguage);
        if (!_settings.Translation.Enabled)
            _engine.ConfigureTranslation(null, target, T("Translation off (Settings > General)", "Traduction désactivée (Paramètres > Général)"));
        else if (deepLKeys.Count == 0)
            _engine.ConfigureTranslation(null, target, T("Translation: no DeepL key (Settings > DeepL)", "Traduction : pas de clé DeepL (Paramètres > DeepL)"));
        else
            _engine.ConfigureTranslation(new FailoverTranslator("DeepL", deepLKeys.Select(key => (ITranslator)new DeepLTranslator(key)).ToList()), target);
    }

    private void ShowLine(OverlayWindow overlay, DisplayLine line)
    {
        IReadOnlyList<OverlaySpeaker> speakers = line.Speakers
            .Select(m => new OverlaySpeaker(m.DisplayName, SpeakerColors.For(m.Id), Avatar(m.AvatarUrl)))
            .ToList();
        string language = line.Transcription.Language;
        // Voix d'un jeu ou d'une autre appli : son nom et la langue, ex. « War Dogs · Anglais ».
        if (line.OtherApp is not null)
            speakers = [new OverlaySpeaker($"{line.OtherApp} · {SpokenLanguages.Name(language)}", SpeakerColors.For(line.OtherApp), null)];
        // Personne d'identifié (RPC absent, ou voix sans événement Discord) : la langue sert d'étiquette.
        else if (speakers.Count == 0)
            speakers = [new OverlaySpeaker(SpokenLanguages.Name(language), SpeakerColors.For(language), null)];
        overlay.AddLine(speakers, line.Text, line.Original);
    }

    // Un avatar n'est téléchargé qu'une fois par session ; WPF le charge en arrière-plan et
    // l'affiche dès qu'il arrive.
    private readonly Dictionary<string, ImageSource> _avatars = new();

    private ImageSource Avatar(string url)
    {
        if (!_avatars.TryGetValue(url, out var image))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(url);
            bitmap.DecodePixelWidth = 48;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            _avatars[url] = image = bitmap;
        }
        return image;
    }

    private void StartDemo(OverlayWindow overlay)
    {
        overlay.SetStatus(T("Demo mode: fake lines, no Discord and no Whisper.", "Mode démo : lignes fictives, ni Discord ni Whisper."));
        var carlos = DemoSpeaker("Carlos", 0);
        var jonas = DemoSpeaker("Jonas", 1);
        var mia = DemoSpeaker("Mia", 2);
        (OverlaySpeaker[] Speakers, string Text, string Original)[] lines =
        [
            ([carlos], "They're coming from the left, watch the bridge!", "¡Vienen por la izquierda, vigilad el puente!"),
            ([jonas], "I'm out of ammo, can someone drop me a bag?", "Ich hab keine Munition mehr, kann mir jemand eine Tasche droppen?"),
            ([mia], "Nice one, let's take point B now.", "Bien joué, on prend le point B maintenant."),
            ([carlos, jonas], "Watch out, behind you!", "¡Cuidado, detrás de ti!"),
        ];
        int tick = 0;
        _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _demoTimer.Tick += (_, _) =>
        {
            // Un tick sur deux : « en cours... », puis la ligne au tick suivant.
            if (tick % 2 == 0)
            {
                overlay.SetBusy(true);
            }
            else
            {
                overlay.SetBusy(false);
                var line = lines[tick / 2 % lines.Length];
                overlay.AddLine(line.Speakers, line.Text, line.Original);
            }
            tick++;
        };
        _demoTimer.Start();
    }

    // Avatars par défaut de Discord, pour voir le rendu sans être connecté.
    private OverlaySpeaker DemoSpeaker(string name, int defaultAvatar) =>
        new(name, SpeakerColors.For(name), Avatar($"https://cdn.discordapp.com/embed/avatars/{defaultAvatar}.png"));

    protected override void OnExit(ExitEventArgs e)
    {
        _demoTimer?.Stop();
        _tray?.Dispose();
        if (_ownsInstance)
            SaveSettings();
        // Sur un thread de fond : bloquer le thread de l'interface en attendant des tâches
        // qui voudraient y revenir provoquerait un blocage à la fermeture. Et jamais plus de
        // 5 s : un processus qui traîne sans icône garderait la place d'une nouvelle instance.
        if (_engine is not null)
            Task.Run(() => _engine.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        if (_ownsInstance)
            _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
