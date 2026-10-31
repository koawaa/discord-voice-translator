using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DiscordVoiceTranslator.Core.Translation;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.App;

/// <summary>
/// Tu tapes dans ta langue, la traduction part dans le presse-papiers : il ne reste qu'à la
/// coller dans le chat Discord. La langue proposée est celle que tu viens d'entendre.
/// </summary>
public partial class ReplyWindow : Window
{
    private readonly Func<string, TargetLanguage, CancellationToken, Task<string>> _translate;
    private readonly CancellationTokenSource _cts = new();

    internal ReplyWindow(TargetLanguage language, Func<string, TargetLanguage, CancellationToken, Task<string>> translate)
    {
        InitializeComponent();
        _translate = translate;
        LanguageBox.ItemsSource = TargetLanguage.All;
        LanguageBox.SelectedItem = language;
        Loaded += (_, _) => InputBox.Focus();
        Closed += (_, _) => _cts.Cancel();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) =>
        Interop.NativeWindow.UseDarkTitleBar(new WindowInteropHelper(this).Handle, ((SolidColorBrush)FindResource("SidebarBrush")).Color);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
        base.OnKeyDown(e);
    }

    // Entrée traduit, Maj+Entrée va à la ligne.
    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            _ = TranslateAsync();
        }
        else if (e.Key == Key.Enter)
        {
            int caret = InputBox.CaretIndex;
            InputBox.Text = InputBox.Text.Insert(caret, Environment.NewLine);
            InputBox.CaretIndex = caret + Environment.NewLine.Length;
            e.Handled = true;
        }
    }

    private void Translate_Click(object sender, RoutedEventArgs e) => _ = TranslateAsync();

    private async Task TranslateAsync()
    {
        string text = InputBox.Text.Trim();
        if (text.Length == 0 || LanguageBox.SelectedItem is not TargetLanguage language)
            return;

        TranslateButton.IsEnabled = false;
        ResultCard.Visibility = Visibility.Visible;
        ResultStatus.Foreground = (Brush)FindResource("TextMutedBrush");
        ResultStatus.Text = T("Translating...", "Traduction...");
        try
        {
            string translated = await _translate(text, language, _cts.Token);
            ResultBox.Text = translated;
            ResultStatus.Text = TryCopy(translated)
                ? T("Copied! Paste it in Discord with Ctrl+V.", "Copiée ! Colle-la dans Discord avec Ctrl+V.")
                : T("The clipboard is busy: select the text above and copy it.", "Le presse-papiers est occupé : sélectionne le texte ci-dessus et copie-le.");
            ResultStatus.Foreground = (Brush)FindResource("SuccessBrush");
        }
        catch (TranslationException ex)
        {
            ResultBox.Text = "";
            ResultStatus.Text = ex.Message;
            ResultStatus.Foreground = (Brush)FindResource("DangerBrush");
        }
        catch (OperationCanceledException)
        {
            // Fenêtre fermée pendant la traduction.
        }
        finally
        {
            TranslateButton.IsEnabled = true;
        }
    }

    private static bool TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
