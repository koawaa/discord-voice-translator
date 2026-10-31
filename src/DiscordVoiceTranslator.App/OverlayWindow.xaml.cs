using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DiscordVoiceTranslator.App.Interop;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.App;

/// <summary>Une personne à afficher en tête de ligne.</summary>
internal sealed record OverlaySpeaker(string Name, Brush Color, ImageSource? Avatar);

public partial class OverlayWindow : Window
{
    private readonly OverlaySettings _settings;
    private readonly OverlayResizer _resizer;
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IntPtr _hwnd;
    private GlobalHotkeys? _hotkeys;
    private bool _editMode;

    /// <summary>Bouton « Settings... » du mode édition.</summary>
    public event Action? SettingsRequested;

    /// <summary>Bouton pause du mode édition, ou Ctrl+Alt+F11.</summary>
    public event Action? PauseToggleRequested;

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
        SetPaused(false);

        _resizer = new OverlayResizer(this, ResizeHandles);
        _resizer.Dragging += size =>
        {
            SizeText.Text = $"{size.Width:0} × {size.Height:0}";
            SizeBadge.Visibility = Visibility.Visible;
        };
        _resizer.DragEnded += () => SizeBadge.Visibility = Visibility.Collapsed;

        SourceInitialized += OnSourceInitialized;
        // Les boutons gardent leurs clics : seul le reste de la surface sert à déplacer.
        MouseLeftButtonDown += (_, _) => { if (_editMode) _resizer.BeginDrag(Edges.All, this); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        _hotkeys = new GlobalHotkeys(_hwnd);
        bool editOk = _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.F9, ToggleEditMode);
        bool hideOk = _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.F10, ToggleVisibility);
        bool pauseOk = _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.F11, () => PauseToggleRequested?.Invoke());
        string taken = T(" (ALREADY IN USE)", " (DÉJÀ PRIS)");
        HotkeyHelp.Text = T("Ctrl+Alt+F9: lock / edit", "Ctrl+Alt+F9 : verrouiller / éditer") + (editOk ? "" : taken)
            + "   ·   " + T("Ctrl+Alt+F10: hide / show", "Ctrl+Alt+F10 : masquer / afficher") + (hideOk ? "" : taken)
            + "   ·   " + T("Ctrl+Alt+F11: pause", "Ctrl+Alt+F11 : pause") + (pauseOk ? "" : taken);

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

    /// <summary>Taille d'origine, en bas au centre de l'écran principal.</summary>
    internal void ResetPosition()
    {
        var defaults = new OverlaySettings();
        Width = _settings.Width = defaults.Width;
        Height = _settings.Height = defaults.Height;
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

    /// <summary>Rend l'overlay visible et saisissable, quel que soit son état.</summary>
    internal void ShowForEditing()
    {
        if (!IsVisible) Show();
        if (!_editMode) SetEditMode(true);
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
        ResizeHandles.Visibility = visibility;
        Cursor = on ? Cursors.SizeAll : null;
        UpdatePlaceholder();
        // Verrouillé en plein glissement (Ctrl+Alt+F9) : on lâche la fenêtre.
        if (!on)
            Mouse.Capture(null);
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

    internal void SetPaused(bool paused)
    {
        PausedIndicator.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = paused ? T("▶ Resume", "▶ Reprendre") : T("❚❚ Pause", "❚❚ Pause");
    }

    /// <summary>Ajoute une prise de parole, dessinée par le thème choisi.</summary>
    internal void AddLine(IReadOnlyList<OverlaySpeaker> speakers, string text, string? original)
    {
        var line = SubtitleTheme.Find(_settings.Theme).BuildLine(
            speakers, text, _settings.ShowOriginal ? original : null, _settings.FontSize, _settings.BackgroundOpacity);
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

    private void Pause_Click(object sender, RoutedEventArgs e) => PauseToggleRequested?.Invoke();

    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        _hotkeys?.Dispose();
        base.OnClosed(e);
    }
}
