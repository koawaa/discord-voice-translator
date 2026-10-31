using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.App;

/// <summary>Une ligne sous-titrée, gardée pour l'historique.</summary>
/// <param name="SpeakerId">ID Discord si une seule personne parlait : elle peut alors être masquée.</param>
internal sealed record HistoryEntry(DateTime Time, string Speaker, Brush SpeakerBrush, string? SpeakerId, string Text, string? Original)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public Visibility OriginalVisibility => Original is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HideVisibility => SpeakerId is null ? Visibility.Collapsed : Visibility.Visible;

    public override string ToString() =>
        $"[{TimeText}] {Speaker} : {Text}" + (Original is null ? "" : $"\n           ({Original})");
}

/// <summary>
/// Les dernières lignes de la session, pour relire ce qui est passé trop vite. Rien n'est
/// écrit sur le disque, sauf export demandé.
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly ObservableCollection<HistoryEntry> _entries;

    /// <summary>Bouton « masquer » d'une ligne : ne plus sous-titrer cette personne.</summary>
    internal event Action<HistoryEntry>? HideRequested;

    internal HistoryWindow(ObservableCollection<HistoryEntry> entries)
    {
        InitializeComponent();
        _entries = entries;
        Entries.ItemsSource = entries;
        entries.CollectionChanged += OnEntriesChanged;
        Closed += (_, _) => entries.CollectionChanged -= OnEntriesChanged;
        Loaded += (_, _) => Scroller.ScrollToEnd();
        UpdateEmpty();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) =>
        Interop.NativeWindow.UseDarkTitleBar(new WindowInteropHelper(this).Handle, ((SolidColorBrush)FindResource("SidebarBrush")).Color);

    // Suit les nouvelles lignes, sauf si on est remonté lire plus haut.
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        bool atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 24;
        UpdateEmpty();
        if (atBottom)
            Dispatcher.BeginInvoke(Scroller.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateEmpty() => EmptyText.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    internal void ShowStatus(string message) => StatusText.Text = message;

    private void Copy_Click(object sender, RoutedEventArgs e) => Copy(Entry(sender).Text, T("Line copied.", "Ligne copiée."));

    private void CopyAll_Click(object sender, RoutedEventArgs e) =>
        Copy(string.Join("\n", _entries), T($"{_entries.Count} line(s) copied.", $"{_entries.Count} ligne(s) copiée(s)."));

    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(Entry(sender));

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _entries.Clear();
        ShowStatus(T("History cleared.", "Historique effacé."));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = T("subtitles", "sous-titres") + $"-{DateTime.Now:yyyy-MM-dd-HHmm}.txt",
            Filter = T("Text file", "Fichier texte") + " (*.txt)|*.txt",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, _entries));
            ShowStatus(T($"Saved to {dialog.FileName}", $"Enregistré dans {dialog.FileName}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(T("Could not save: ", "Impossible d'enregistrer : ") + ex.Message);
        }
    }

    // Le presse-papiers peut être tenu un instant par une autre appli : on le dit plutôt que de planter.
    private void Copy(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            ShowStatus(done);
        }
        catch (ExternalException)
        {
            ShowStatus(T("The clipboard is busy, try again.", "Le presse-papiers est occupé, réessaie."));
        }
    }

    private static HistoryEntry Entry(object sender) => (HistoryEntry)((FrameworkElement)sender).DataContext;
}
