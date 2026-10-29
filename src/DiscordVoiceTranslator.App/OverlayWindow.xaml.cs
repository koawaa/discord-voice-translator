using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DiscordVoiceTranslator.App.Interop;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.App;

/// <summary>Une personne à afficher en tête de ligne.</summary>
internal sealed record OverlaySpeaker(string Name, Brush Color, ImageSource? Avatar);

public partial class OverlayWindow : Window
{
    private static readonly Brush TextBrush = Frozen(Colors.White);
    private static readonly Brush OriginalBrush = Frozen(Color.FromRgb(0xB8, 0xB8, 0xB8));

    private readonly OverlaySettings _settings;
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IntPtr _hwnd;
    private GlobalHotkeys? _hotkeys;
    private bool _editMode;

    /// <summary>Bouton « Settings... » du mode édition.</summary>
    public event Action? SettingsRequested;

    /// <summary>L'overlay a été déplacé ou redimensionné (à la sortie du mode édition).</summary>
    public event Action? GeometryChanged;

    internal OverlayWindow(OverlaySettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Width = settings.Width;
        Height = settings.Height;
        PlaceWindow();
        ApplySettings();

        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += (_, _) => { if (_editMode) DragMove(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        _hotkeys = new GlobalHotkeys(_hwnd);
        bool editOk = _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.F9, ToggleEditMode);
        bool hideOk = _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.F10, ToggleVisibility);
        string taken = T(" (ALREADY IN USE)", " (DÉJÀ PRIS)");
        HotkeyHelp.Text = T("Ctrl+Alt+F9: lock / edit", "Ctrl+Alt+F9 : verrouiller / éditer") + (editOk ? "" : taken)
            + "   ·   " + T("Ctrl+Alt+F10: hide / show", "Ctrl+Alt+F10 : masquer / afficher") + (hideOk ? "" : taken);

        // Démarre en mode édition pour voir où est l'overlay et l'état du moteur.
        SetEditMode(true);

        _topmostTimer.Tick += (_, _) => NativeWindow.KeepOnTop(_hwnd);
        _topmostTimer.Start();
    }

    /// <summary>Police, nombre de lignes... Les nouvelles lignes prennent aussi taille et fond.</summary>
    internal void ApplySettings()
    {
        FontFamily = new FontFamily(_settings.FontFamily);
        while (Lines.Children.Count > _settings.MaxLines)
            Lines.Children.RemoveAt(0);
    }

    internal void ResetPosition()
    {
        _settings.Left = _settings.Top = null;
        PlaceWindow();
    }

    private void PlaceWindow()
    {
        var area = SystemParameters.WorkArea;
        double left = _settings.Left ?? area.Left + (area.Width - Width) / 2;
        double top = _settings.Top ?? area.Bottom - Height - 80;

        // Position enregistrée sur un écran qui n'existe plus (autre résolution, écran débranché) :
        // on revient au centre plutôt que d'afficher l'overlay hors de vue.
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                               SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screens.IntersectsWith(new Rect(left, top, Width, Height)))
        {
            left = area.Left + (area.Width - Width) / 2;
            top = area.Bottom - Height - 80;
        }
        Left = left;
        Top = top;
    }

    internal void ToggleEditMode()
    {
        // Éditer un overlay masqué n'aurait pas de sens : on le réaffiche d'abord.
        if (!IsVisible) Show();
        SetEditMode(!_editMode);
    }

    internal void ToggleVisibility()
    {
        if (IsVisible) Hide();
        else Show();
    }

    private void SetEditMode(bool on)
    {
        bool leaving = _editMode && !on;
        _editMode = on;
        var visibility = on ? Visibility.Visible : Visibility.Collapsed;
        EditFrame.Visibility = visibility;
        EditHeader.Visibility = visibility;
        ResizeMode = on ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
        UpdatePlaceholder();
        // Après ResizeMode, que WPF applique en retouchant les styles de la fenêtre.
        NativeWindow.SetClickThrough(_hwnd, clickThrough: !on);

        if (leaving)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Width = Width;
            _settings.Height = Height;
            GeometryChanged?.Invoke();
        }
    }

    public void SetStatus(string status) => StatusText.Text = status;

    public void SetBusy(bool busy) => BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Ajoute une prise de parole : avatars et noms colorés (« A / B » si deux voix se
    /// mêlent), texte, original optionnel en petit.
    /// </summary>
    internal void AddLine(IReadOnlyList<OverlaySpeaker> speakers, string text, string? original)
    {
        double size = _settings.FontSize;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 1) };
        foreach (var speaker in speakers.Where(s => s.Avatar is not null))
        {
            header.Children.Add(new Ellipse
            {
                Width = size,
                Height = size,
                Margin = new Thickness(0, 0, 5, 0),
                Fill = new ImageBrush(speaker.Avatar) { Stretch = Stretch.UniformToFill },
            });
        }
        var names = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = size * 0.75, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < speakers.Count; i++)
        {
            if (i > 0)
                names.Inlines.Add(new Run(" / ") { Foreground = OriginalBrush });
            names.Inlines.Add(new Run(speakers[i].Name) { Foreground = speakers[i].Color });
        }
        header.Children.Add(names);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = TextBrush,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
        });
        if (_settings.ShowOriginal && !string.IsNullOrWhiteSpace(original))
        {
            content.Children.Add(new TextBlock
            {
                Text = original,
                Foreground = OriginalBrush,
                FontStyle = FontStyles.Italic,
                FontSize = size * 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var line = new Border
        {
            Background = Frozen(Color.FromArgb((byte)(_settings.BackgroundOpacity * 255), 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 6),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = content,
        };
        Lines.Children.Add(line);
        while (Lines.Children.Count > _settings.MaxLines)
            Lines.Children.RemoveAt(0);
        UpdatePlaceholder();

        // Visible pendant DisplaySeconds, puis s'estompe en 1 s et disparaît. 30 images/s
        // suffisent pour un fondu et évitent de redessiner l'overlay à 60+ Hz pendant le jeu.
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(1)) { BeginTime = TimeSpan.FromSeconds(_settings.DisplaySeconds) };
        Timeline.SetDesiredFrameRate(fade, 30);
        fade.Completed += (_, _) =>
        {
            Lines.Children.Remove(line);
            UpdatePlaceholder();
        };
        line.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdatePlaceholder() =>
        Placeholder.Visibility = _editMode && Lines.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        _hotkeys?.Dispose();
        base.OnClosed(e);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
