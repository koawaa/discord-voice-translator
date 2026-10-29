using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using DiscordVoiceTranslator.Core.Config;
using DiscordVoiceTranslator.Core.Discord;
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
    private readonly string? _storedDeepLKey = SecretStore.Load(TranslationSettings.DeepLSecretName);
    private readonly DiscordAppCredentials? _storedDiscord = DiscordAppCredentials.Load();
    private bool _forgotAuthorization;

    internal bool TranslationChanged { get; private set; }
    internal bool DiscordChanged { get; private set; }
    internal bool RestartNeeded { get; private set; }
    internal string? DeepLKey { get; private set; }
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
                Width = 120,
                Margin = new Thickness(0, 4, 8, 0),
            });
        }
        SilenceSlider.Value = settings.Transcription.EndOfSentenceSilenceMs;
        UseGpuBox.IsChecked = settings.Transcription.UseGpu;

        DeepLKeyHint.Text = _storedDeepLKey is null
            ? T("It will be encrypted with your Windows account (DPAPI).", "Elle sera chiffrée avec ton compte Windows (DPAPI).")
            : T("A key is saved (encrypted). Leave empty to keep it.", "Une clé est enregistrée (chiffrée). Laisse vide pour la garder.");
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

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        // 1. Tout vérifier avant d'enregistrer quoi que ce soit.
        var allowed = LanguagesPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        if (allowed.Count == 0)
        {
            Fail(TranscriptionTab, T("Tick at least one language.", "Coche au moins une langue."));
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

        bool translate = TranslateBox.IsChecked == true;
        string? typedKey = Typed(DeepLKeyBox.Password);
        string? key = typedKey ?? _storedDeepLKey;
        if (translate && key is null)
        {
            Fail(DeepLTab, T("Enter a DeepL key, or untick \"Translate\" in General.", "Entre une clé DeepL, ou décoche « Traduire » dans Général."));
            return;
        }
        if (typedKey is not null)
        {
            SaveButton.IsEnabled = false;
            ShowInfo(T("Checking the DeepL key...", "Vérification de la clé DeepL..."));
            var (ok, message) = await CheckDeepLKeyAsync(typedKey);
            SaveButton.IsEnabled = true;
            if (!ok)
            {
                Fail(DeepLTab, message);
                return;
            }
        }

        // 2. Secrets.
        if (typedKey is not null)
            SecretStore.Save(TranslationSettings.DeepLSecretName, typedKey);
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
            || useGpu != transcription.UseGpu;
        TranslationChanged = translate != _settings.Translation.Enabled || target != _settings.Translation.TargetLanguage || typedKey is not null;

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
        _settings.Discord.SpeakerMarginMs = (int)MarginSlider.Value;

        DeepLKey = translate ? key : null;
        Discord = discord;
        DialogResult = true;
    }

    private async void CheckKey_Click(object sender, RoutedEventArgs e)
    {
        string? key = Typed(DeepLKeyBox.Password) ?? _storedDeepLKey;
        if (key is null)
        {
            DeepLResult.Text = T("Paste a key first.", "Colle d'abord une clé.");
            return;
        }
        CheckKeyButton.IsEnabled = false;
        DeepLResult.Foreground = Brushes.DimGray;
        DeepLResult.Text = T("Checking...", "Vérification...");
        var (ok, message) = await CheckDeepLKeyAsync(key);
        DeepLResult.Foreground = ok ? Brushes.SeaGreen : Brushes.Firebrick;
        DeepLResult.Text = message;
        CheckKeyButton.IsEnabled = true;
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
        ErrorText.Foreground = Brushes.Firebrick;
        ErrorText.Text = message;
    }

    private void ShowInfo(string message)
    {
        ErrorText.Foreground = Brushes.DimGray;
        ErrorText.Text = message;
    }

    private static string? Typed(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
