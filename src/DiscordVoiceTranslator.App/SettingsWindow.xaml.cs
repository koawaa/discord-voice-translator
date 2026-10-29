using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using DiscordVoiceTranslator.Core.Audio;
using DiscordVoiceTranslator.Core.Config;
using DiscordVoiceTranslator.Core.Discord;
using DiscordVoiceTranslator.Core.Models;
using DiscordVoiceTranslator.Core.Translation;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.App;

/// <summary>
/// Tous les réglages. « Enregistrer » met à jour <see cref="AppSettings"/> et les secrets ;
/// App lit ensuite les indicateurs ci-dessous pour savoir quoi réappliquer.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<string> _storedDeepLKeys = TranslationSettings.LoadDeepLKeys();
    private readonly ObservableCollection<KeyRow> _keys = [];
    private readonly ObservableCollection<CapturedApp> _apps;
    private readonly DiscordAppCredentials? _storedDiscord = DiscordAppCredentials.Load();
    private bool _forgotAuthorization;

    internal bool TranslationChanged { get; private set; }
    internal bool DiscordChanged { get; private set; }
    internal bool AudioChanged { get; private set; }
    internal bool RestartNeeded { get; private set; }
    internal IReadOnlyList<string> DeepLKeys { get; private set; } = [];
    internal DiscordAppCredentials? Discord { get; private set; }

    /// <summary>« Remettre l'overlay en bas au centre » : appliqué tout de suite.</summary>
    internal event Action? ResetPositionRequested;

    internal SettingsWindow(AppSettings settings, Func<string> currentStatus)
    {
        InitializeComponent();
        _settings = settings;

        UiLanguageBox.SelectedIndex = settings.UiLanguage == "fr" ? 1 : 0;
        TranslateBox.IsChecked = settings.Translation.Enabled;
        TargetBox.ItemsSource = TargetLanguage.All;
        TargetBox.SelectedItem = TargetLanguage.Find(settings.Translation.TargetLanguage);
        ShowOriginalBox.IsChecked = settings.Overlay.ShowOriginal;

        CaptureDiscordBox.IsChecked = settings.Audio.CaptureDiscord;
        _apps = new ObservableCollection<CapturedApp>(settings.Audio.Apps);
        AppsList.ItemsSource = _apps;
        _apps.CollectionChanged += (_, _) => NoAppsText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoAppsText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = RefreshRunningAppsAsync();

        var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(f => f).ToList();
        if (!fonts.Contains(settings.Overlay.FontFamily))
            fonts.Insert(0, settings.Overlay.FontFamily);
        FontBox.ItemsSource = fonts;
        FontBox.SelectedItem = settings.Overlay.FontFamily;
        FontSizeSlider.Value = settings.Overlay.FontSize;
        OpacitySlider.Value = settings.Overlay.BackgroundOpacity * 100;
        LinesSlider.Value = settings.Overlay.MaxLines;
        DurationSlider.Value = settings.Overlay.DisplaySeconds;

        foreach (var (code, english, french) in SpokenLanguages.All)
        {
            LanguagesPanel.Children.Add(new CheckBox
            {
                Content = T(english, french),
                Tag = code,
                IsChecked = settings.Transcription.AllowedLanguages.Contains(code),
                Style = (Style)FindResource("Chip"),
            });
        }
        SilenceSlider.Value = settings.Transcription.EndOfSentenceSilenceMs;
        UseGpuBox.IsChecked = settings.Transcription.UseGpu;

        string selectedModel = WhisperModel.Find(settings.Transcription.Model).Id;
        foreach (var model in WhisperModel.All)
        {
            var content = new StackPanel { Children =
            {
                new TextBlock { Text = model.DisplayName, Style = (Style)FindResource("Label") },
                new TextBlock { Text = model.Details, Style = (Style)FindResource("Hint") },
            } };
            if (model.File.IsDownloaded(TranslatorEngine.ModelsDir))
            {
                var downloaded = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Foreground = ThemeBrush("SuccessBrush") };
                downloaded.Inlines.Add(new System.Windows.Documents.Run("  ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10 });
                downloaded.Inlines.Add(new System.Windows.Documents.Run(T("Already downloaded", "Déjà téléchargé")));
                content.Children.Add(downloaded);
            }
            ModelsPanel.Children.Add(new RadioButton
            {
                Style = (Style)FindResource("Option"),
                GroupName = "Model",
                Tag = model.Id,
                IsChecked = model.Id == selectedModel,
                Content = content,
            });
        }

        foreach (string key in _storedDeepLKeys)
            _keys.Add(new KeyRow(key) { Status = T("Saved (encrypted)", "Enregistrée (chiffrée)"), StatusBrush = ThemeBrush("TextMutedBrush") });
        KeysList.ItemsSource = _keys;
        _keys.CollectionChanged += (_, _) => RefreshKeys();
        RefreshKeys();
        ClientIdBox.Text = _storedDiscord?.ClientId ?? "";
        ClientSecretHint.Text = _storedDiscord is null
            ? T("It will be encrypted with your Windows account (DPAPI).", "Il sera chiffré avec ton compte Windows (DPAPI).")
            : T("A secret is saved (encrypted). Leave empty to keep it.", "Un secret est enregistré (chiffré). Laisse vide pour le garder.");
        MarginSlider.Value = settings.Discord.SpeakerMarginMs;

        // État du moteur (VRAM, latence, runtime...) rafraîchi en direct pendant que la fenêtre est ouverte.
        StatusBox.Text = currentStatus();
        _statusTimer.Tick += (_, _) => StatusBox.Text = currentStatus();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
    }

    // Barre de titre sombre, assortie au reste de la fenêtre.
    private void Window_SourceInitialized(object? sender, EventArgs e) =>
        Interop.NativeWindow.UseDarkTitleBar(new WindowInteropHelper(this).Handle, ((SolidColorBrush)FindResource("SidebarBrush")).Color);

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        // 1. Tout vérifier avant d'enregistrer quoi que ce soit.
        var allowed = LanguagesPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        if (allowed.Count == 0)
        {
            Fail(TranscriptionTab, T("Tick at least one language.", "Coche au moins une langue."));
            return;
        }
        string model = ModelsPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? WhisperModel.DefaultId;
        bool captureDiscord = CaptureDiscordBox.IsChecked == true;
        if (!captureDiscord && _apps.Count == 0)
        {
            Fail(AudioTab, T("Choose at least one app to subtitle: Discord or another app.", "Choisis au moins une appli à sous-titrer : Discord ou une autre appli."));
            return;
        }

        string clientId = ClientIdBox.Text.Trim();
        string? typedSecret = Typed(ClientSecretBox.Password);
        DiscordAppCredentials? discord = null;
        if (clientId.Length > 0)
        {
            if (!ulong.TryParse(clientId, out _))
            {
                Fail(DiscordTab, T("The Client ID is a number of about 19 digits.", "Le Client ID est un nombre d'environ 19 chiffres."));
                return;
            }
            // Le secret enregistré n'appartient qu'à l'application enregistrée.
            string? secret = typedSecret ?? (_storedDiscord?.ClientId == clientId ? _storedDiscord.ClientSecret : null);
            if (secret is null)
            {
                Fail(DiscordTab, T("Paste the Client Secret.", "Colle le Client Secret."));
                return;
            }
            discord = new DiscordAppCredentials(clientId, secret);
        }

        // Une clé collée sans cliquer sur « Ajouter » : on l'ajoute plutôt que de la perdre.
        if (Typed(DeepLKeyBox.Password) is not null)
        {
            SaveButton.IsEnabled = false;
            ShowInfo(T("Checking the DeepL key...", "Vérification de la clé DeepL..."));
            string? error = await AddTypedKeyAsync();
            SaveButton.IsEnabled = true;
            if (error is not null)
            {
                Fail(DeepLTab, error);
                return;
            }
        }
        bool translate = TranslateBox.IsChecked == true;
        var keys = _keys.Select(row => row.Key).ToList();
        if (translate && keys.Count == 0)
        {
            Fail(DeepLTab, T("Add a DeepL key, or untick \"Translate\" in General.", "Ajoute une clé DeepL, ou décoche « Traduire » dans Général."));
            return;
        }

        // 2. Secrets.
        bool keysChanged = !keys.SequenceEqual(_storedDeepLKeys);
        if (keysChanged)
            TranslationSettings.SaveDeepLKeys(keys);
        DiscordChanged = discord != _storedDiscord || _forgotAuthorization;
        if (discord != _storedDiscord)
        {
            // Une autorisation appartient à une application : on l'oublie si elle change.
            if (discord?.ClientId != _storedDiscord?.ClientId)
                DiscordToken.Forget();
            if (discord is null) DiscordAppCredentials.Delete();
            else discord.Save();
        }

        // 3. Réglages ordinaires.
        string uiLanguage = (string)((ComboBoxItem)UiLanguageBox.SelectedItem).Tag;
        string target = ((TargetLanguage)TargetBox.SelectedItem).Code;
        int silence = (int)SilenceSlider.Value;
        bool useGpu = UseGpuBox.IsChecked == true;
        var transcription = _settings.Transcription;

        RestartNeeded = uiLanguage != _settings.UiLanguage
            || !allowed.SequenceEqual(transcription.AllowedLanguages)
            || silence != transcription.EndOfSentenceSilenceMs
            || useGpu != transcription.UseGpu
            || model != WhisperModel.Find(transcription.Model).Id;
        TranslationChanged = translate != _settings.Translation.Enabled || target != _settings.Translation.TargetLanguage || keysChanged;

        AudioChanged = captureDiscord != _settings.Audio.CaptureDiscord || !_apps.SequenceEqual(_settings.Audio.Apps);
        _settings.Audio.CaptureDiscord = captureDiscord;
        _settings.Audio.Apps = _apps.ToList();

        _settings.UiLanguage = uiLanguage;
        _settings.Translation.Enabled = translate;
        _settings.Translation.TargetLanguage = target;
        _settings.Overlay.ShowOriginal = ShowOriginalBox.IsChecked == true;
        _settings.Overlay.FontFamily = (string)FontBox.SelectedItem;
        _settings.Overlay.FontSize = FontSizeSlider.Value;
        _settings.Overlay.BackgroundOpacity = OpacitySlider.Value / 100;
        _settings.Overlay.MaxLines = (int)LinesSlider.Value;
        _settings.Overlay.DisplaySeconds = DurationSlider.Value;
        transcription.AllowedLanguages = allowed;
        transcription.EndOfSentenceSilenceMs = silence;
        transcription.UseGpu = useGpu;
        transcription.Model = model;
        _settings.Discord.SpeakerMarginMs = (int)MarginSlider.Value;

        DeepLKeys = translate ? keys : [];
        Discord = discord;
        DialogResult = true;
    }

    // Lister les processus prend quelques centaines de ms : hors du thread de l'interface.
    private async Task RefreshRunningAppsAsync()
    {
        RefreshAppsButton.IsEnabled = false;
        var running = await Task.Run(ProcessLocator.ListWindowedApps);
        RunningAppsBox.ItemsSource = running;
        RunningAppsBox.SelectedIndex = running.Count > 0 ? 0 : -1;
        RefreshAppsButton.IsEnabled = true;
    }

    private async void RefreshApps_Click(object sender, RoutedEventArgs e)
    {
        ShowAppsError("");
        await RefreshRunningAppsAsync();
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        if (RunningAppsBox.SelectedItem is not RunningApp app)
        {
            ShowAppsError(T("Pick an app in the list (launch it first if it is missing).", "Choisis une appli dans la liste (lance-la d'abord si elle n'y est pas)."));
            return;
        }
        if (_apps.Any(a => a.ExeName.Equals(app.ExeName, StringComparison.OrdinalIgnoreCase)))
        {
            ShowAppsError(T("This app is already in the list.", "Cette appli est déjà dans la liste."));
            return;
        }
        // Le titre de la fenêtre sert d'étiquette sur l'overlay : on le garde court.
        string name = app.Title.Length > 40 ? app.Title[..40].TrimEnd() + "…" : app.Title;
        _apps.Add(new CapturedApp(app.ExeName, name));
        ShowAppsError("");
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e) =>
        _apps.Remove((CapturedApp)((FrameworkElement)sender).DataContext);

    private void ShowAppsError(string message)
    {
        AppsResult.Text = message;
        AppsResult.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void AddKey_Click(object sender, RoutedEventArgs e)
    {
        if (Typed(DeepLKeyBox.Password) is null)
        {
            ShowKeyError(T("Paste a key first.", "Colle d'abord une clé."));
            return;
        }
        AddKeyButton.IsEnabled = false;
        DeepLResult.Foreground = ThemeBrush("TextMutedBrush");
        DeepLResult.Text = T("Checking...", "Vérification...");
        string? error = await AddTypedKeyAsync();
        AddKeyButton.IsEnabled = true;
        if (error is null) DeepLResult.Text = "";
        else ShowKeyError(error);
    }

    /// <summary>Vérifie la clé collée et l'ajoute en bas de la liste ; renvoie l'erreur sinon.</summary>
    private async Task<string?> AddTypedKeyAsync()
    {
        string key = Typed(DeepLKeyBox.Password)!;
        if (_keys.Any(row => row.Key == key))
            return T("This key is already in the list.", "Cette clé est déjà dans la liste.");
        var (ok, message) = await CheckDeepLKeyAsync(key);
        if (!ok)
            return message;
        _keys.Add(new KeyRow(key) { Status = message, StatusBrush = ThemeBrush("SuccessBrush") });
        DeepLKeyBox.Clear();
        return null;
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e) =>
        _keys.Remove((KeyRow)((FrameworkElement)sender).DataContext);

    private async void CheckKey_Click(object sender, RoutedEventArgs e)
    {
        if (_keys.Count == 0)
        {
            ShowKeyError(T("Add a key first.", "Ajoute d'abord une clé."));
            return;
        }
        CheckKeyButton.IsEnabled = false;
        DeepLResult.Text = "";
        foreach (var row in _keys.ToList())
        {
            row.Status = T("Checking...", "Vérification...");
            row.StatusBrush = ThemeBrush("TextMutedBrush");
            var (ok, message) = await CheckDeepLKeyAsync(row.Key);
            row.Status = message;
            row.StatusBrush = ThemeBrush(ok ? "SuccessBrush" : "DangerBrush");
        }
        CheckKeyButton.IsEnabled = true;
    }

    private void ShowKeyError(string message)
    {
        DeepLResult.Foreground = ThemeBrush("DangerBrush");
        DeepLResult.Text = message;
    }

    // Numéros 1, 2, 3... dans l'ordre d'utilisation.
    private void RefreshKeys()
    {
        for (int i = 0; i < _keys.Count; i++)
            _keys[i].Number = i + 1;
        NoKeysText.Visibility = _keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Consulter le quota ne consomme aucun caractère : c'est la bonne façon de tester une clé.
    private static async Task<(bool Ok, string Message)> CheckDeepLKeyAsync(string key)
    {
        try
        {
            using var translator = new DeepLTranslator(key);
            var usage = await translator.GetUsageAsync(CancellationToken.None);
            return (true, usage is null
                ? T("Valid key.", "Clé valide.")
                : usage.CharactersUsed >= usage.CharacterLimit
                ? T($"Valid key, but this month's quota is used up ({usage.CharacterLimit:N0} characters)",
                    $"Clé valide, mais le quota du mois est épuisé ({usage.CharacterLimit:N0} caractères)")
                : T($"Valid key · {usage.CharactersUsed:N0} / {usage.CharacterLimit:N0} characters used this month",
                    $"Clé valide · {usage.CharactersUsed:N0} / {usage.CharacterLimit:N0} caractères utilisés ce mois"));
        }
        catch (TranslationException ex)
        {
            return (false, ex.Message);
        }
    }

    private void ForgetAuthorization_Click(object sender, RoutedEventArgs e)
    {
        DiscordToken.Forget();
        _forgotAuthorization = true;
        ShowInfo(T("Done. Discord will ask for authorization again after you save.",
                   "C'est fait. Discord redemandera l'autorisation après l'enregistrement."));
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e) => ResetPositionRequested?.Invoke();

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Fail(TabItem tab, string message)
    {
        Tabs.SelectedItem = tab;
        ErrorText.Foreground = ThemeBrush("DangerBrush");
        ErrorText.Text = message;
    }

    private void ShowInfo(string message)
    {
        ErrorText.Foreground = ThemeBrush("TextMutedBrush");
        ErrorText.Text = message;
    }

    private Brush ThemeBrush(string key) => (Brush)FindResource(key);

    private static string? Typed(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Une ligne de la liste des clés DeepL : la clé n'est jamais affichée en entier.</summary>
    private sealed class KeyRow(string key) : INotifyPropertyChanged
    {
        private int _number;
        private string _status = "";
        private Brush? _statusBrush;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Key { get; } = key;

        private bool IsFree => Key.EndsWith(":fx", StringComparison.Ordinal);

        // Assez pour reconnaître la clé sur la page du compte DeepL, pas pour la réutiliser.
        public string Masked => "••••••••" + Key[Math.Max(0, Key.Length - (IsFree ? 7 : 4))..] + (IsFree ? "  ·  Free" : "  ·  Pro");

        public int Number { get => _number; set => Set(ref _number, value, nameof(Number)); }
        public string Status { get => _status; set => Set(ref _status, value, nameof(Status)); }
        public Brush? StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value, nameof(StatusBrush)); }

        private void Set<TValue>(ref TValue field, TValue value, string name)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
