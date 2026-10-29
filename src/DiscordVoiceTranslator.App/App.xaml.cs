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
        _instanceMutex = new Mutex(false, @"Local\DiscordVoiceTranslator");
        try
        {
            _ownsInstance = _instanceMutex.WaitOne(e.Args.Contains(RestartedArg) ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstance = true; // l'ancienne instance a planté : la place est libre
        }
        if (!_ownsInstance)
        {
            MessageBox.Show(T("Discord Voice Translator is already running: look for its icon next to the clock (hidden icons arrow).",
                              "Le traducteur vocal Discord est déjà lancé : cherche son icône près de l'horloge (flèche des icônes cachées)."),
                            T("Discord Voice Translator", "Traducteur vocal Discord"));
            Shutdown();
            return;
        }

        // Le jeu passe avant nous pour le CPU.
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;

        var overlay = _overlay = new OverlayWindow(_settings.Overlay);
        overlay.SettingsRequested += OpenSettings;
        overlay.GeometryChanged += SaveSettings;
        overlay.Show();

        _tray = new TrayIcon();
        _tray.EditModeRequested += overlay.ToggleEditMode;
        _tray.VisibilityToggleRequested += overlay.ToggleVisibility;
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
        ApplyTranslation(SecretStore.Load(TranslationSettings.DeepLSecretName));
        _engine.ConfigureDiscord(DiscordAppCredentials.Load(), _settings.Discord.SpeakerMarginMs);

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

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings, () => _engine?.CurrentStatus ?? "");
        window.ResetPositionRequested += () => _overlay?.ResetPosition();
        if (window.ShowDialog() != true)
            return;

        SaveSettings();
        _overlay?.ApplySettings();
        if (window.TranslationChanged)
            ApplyTranslation(window.DeepLKey);
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

    private void ApplyTranslation(string? deepLKey)
    {
        if (_engine is null)
            return;
        var target = TargetLanguage.Find(_settings.Translation.TargetLanguage);
        if (!_settings.Translation.Enabled)
            _engine.ConfigureTranslation(null, target, T("Translation off (Settings > General)", "Traduction désactivée (Paramètres > Général)"));
        else if (deepLKey is null)
            _engine.ConfigureTranslation(null, target, T("Translation: no DeepL key (Settings > DeepL)", "Traduction : pas de clé DeepL (Paramètres > DeepL)"));
        else
            _engine.ConfigureTranslation(new DeepLTranslator(deepLKey), target);
    }

    private void ShowLine(OverlayWindow overlay, DisplayLine line)
    {
        IReadOnlyList<OverlaySpeaker> speakers = line.Speakers
            .Select(m => new OverlaySpeaker(m.DisplayName, SpeakerColors.For(m.Id), Avatar(m.AvatarUrl)))
            .ToList();
        // Personne d'identifié (RPC absent, ou voix sans événement Discord) : la langue sert d'étiquette.
        if (speakers.Count == 0)
        {
            string language = line.Transcription.Language;
            speakers = [new OverlaySpeaker(SpokenLanguages.Name(language), SpeakerColors.For(language), null)];
        }
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
        // qui voudraient y revenir provoquerait un blocage à la fermeture.
        if (_engine is not null)
            Task.Run(() => _engine.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        if (_ownsInstance)
            _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
