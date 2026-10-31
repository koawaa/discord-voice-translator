using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using DiscordVoiceTranslator.App.Interop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DiscordVoiceTranslator.App;

/// <summary>Bords qu'entraîne un glissement. Les quatre ensemble : la fenêtre se déplace sans changer de taille.</summary>
[Flags]
internal enum Edges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    All = Left | Top | Right | Bottom,
}

/// <summary>
/// Déplacement et redimensionnement de l'overlay en mode édition : poignées sur les quatre
/// bords et les quatre coins, aimantation aux bords et au centre de l'écran (Maj pour s'en
/// passer), Ctrl pour redimensionner depuis le centre, Échap pour annuler.
/// Tout est calculé en pixels écran et appliqué d'un seul SetWindowPos : régler Left puis
/// Width à part ferait trembler le bord opposé quand on tire par la gauche ou par le haut.
/// </summary>
internal sealed class OverlayResizer
{
    private const double HitThickness = 12;   // bande saisissable le long de chaque bord
    private const double CornerSize = 18;     // carré saisissable dans chaque coin (plus grand, il mordrait sur le bouton ✕)
    private const double FrameLine = 7;       // milieu du trait du cadre d'édition (marge 6 + épaisseur 2 / 2)
    private const double SnapDistance = 12;

    private static readonly Brush Accent = Frozen(Color.FromRgb(0x4F, 0xA3, 0xFF));

    private readonly Window _window;
    private Edges _edges;
    private Drawing.Rectangle _startBounds;
    private Drawing.Point _startCursor;

    /// <summary>Taille en cours (unités WPF), à chaque mouvement.</summary>
    public event Action<Size>? Dragging;

    public event Action? DragEnded;

    public OverlayResizer(Window window, Panel handles)
    {
        _window = window;
        foreach (var (edges, cursor) in new[]
        {
            (Edges.Left, Cursors.SizeWE), (Edges.Right, Cursors.SizeWE),
            (Edges.Top, Cursors.SizeNS), (Edges.Bottom, Cursors.SizeNS),
            (Edges.Left | Edges.Top, Cursors.SizeNWSE), (Edges.Right | Edges.Bottom, Cursors.SizeNWSE),
            (Edges.Right | Edges.Top, Cursors.SizeNESW), (Edges.Left | Edges.Bottom, Cursors.SizeNESW),
        })
        {
            handles.Children.Add(Handle(edges, cursor));
        }

        // Pendant le glissement, la souris est capturée : ces événements arrivent même hors de la fenêtre.
        window.MouseMove += (_, _) => OnMouseMove();
        window.MouseLeftButtonUp += (_, _) => { if (_edges != Edges.None) Mouse.Capture(null); };
        window.LostMouseCapture += (_, _) => EndDrag();
    }

    /// <summary>Commence à tirer les bords donnés (<see cref="Edges.All"/> pour déplacer).</summary>
    public void BeginDrag(Edges edges, UIElement source)
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        _startBounds = NativeWindow.GetBounds(hwnd);
        _startCursor = Forms.Cursor.Position;
        _edges = edges;
        source.CaptureMouse();
        Dragging?.Invoke(new Size(_window.ActualWidth, _window.ActualHeight));
    }

    private void OnMouseMove()
    {
        if (_edges == Edges.None)
            return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (NativeWindow.IsKeyDown(Key.Escape))
        {
            NativeWindow.SetBounds(hwnd, _startBounds);
            Mouse.Capture(null);
            return;
        }

        var cursor = Forms.Cursor.Position;
        double scale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        var bounds = Compute(_edges, _startBounds, cursor.X - _startCursor.X, cursor.Y - _startCursor.Y,
            Forms.Screen.FromPoint(cursor).WorkingArea,
            new Drawing.Size((int)Math.Ceiling(_window.MinWidth * scale), (int)Math.Ceiling(_window.MinHeight * scale)),
            snap: NativeWindow.IsKeyDown(Key.LeftShift) || NativeWindow.IsKeyDown(Key.RightShift) ? 0 : (int)(SnapDistance * scale),
            fromCenter: _edges != Edges.All && (NativeWindow.IsKeyDown(Key.LeftCtrl) || NativeWindow.IsKeyDown(Key.RightCtrl)));
        NativeWindow.SetBounds(hwnd, bounds);
        Dragging?.Invoke(new Size(bounds.Width / scale, bounds.Height / scale));
    }

    private void EndDrag()
    {
        if (_edges == Edges.None)
            return;
        _edges = Edges.None;
        DragEnded?.Invoke();
    }

    /// <summary>
    /// Nouvelle position de la fenêtre après un glissement de (dx, dy) pixels. Séparé de la
    /// souris et de Windows pour rester simple à vérifier.
    /// </summary>
    /// <param name="snap">Distance d'aimantation en pixels ; 0 la désactive.</param>
    /// <param name="fromCenter">Le bord opposé bouge en miroir : la fenêtre garde son centre.</param>
    internal static Drawing.Rectangle Compute(Edges edges, Drawing.Rectangle start, int dx, int dy,
                                              Drawing.Rectangle work, Drawing.Size min, int snap, bool fromCenter)
    {
        if (edges == Edges.All)
        {
            var moved = start with { X = start.X + dx, Y = start.Y + dy };
            // Le bord le plus proche d'un bord de l'écran, ou le centre de la fenêtre sur celui de l'écran.
            int sx = Nearest(snap, work.Left - moved.Left, work.Right - moved.Right, (work.Left + work.Right - moved.Left - moved.Right) / 2);
            int sy = Nearest(snap, work.Top - moved.Top, work.Bottom - moved.Bottom, (work.Top + work.Bottom - moved.Top - moved.Bottom) / 2);
            return moved with { X = moved.X + sx, Y = moved.Y + sy };
        }
        var (left, right) = ResizeAxis(edges.HasFlag(Edges.Left), edges.HasFlag(Edges.Right), start.Left, start.Right, dx, work.Left, work.Right, min.Width, snap, fromCenter);
        var (top, bottom) = ResizeAxis(edges.HasFlag(Edges.Top), edges.HasFlag(Edges.Bottom), start.Top, start.Bottom, dy, work.Top, work.Bottom, min.Height, snap, fromCenter);
        return Drawing.Rectangle.FromLTRB(left, top, right, bottom);
    }

    /// <summary>Un axe : déplace le bord saisi (« bas » = gauche ou haut), l'aimante, puis garde la taille minimale.</summary>
    private static (int Low, int High) ResizeAxis(bool low, bool high, int lo, int hi, int delta,
                                                  int workLo, int workHi, int min, int snap, bool fromCenter)
    {
        if (!low && !high)
            return (lo, hi);

        // Cibles : le bord de l'écran de ce côté et, sauf depuis le centre (déjà symétrique),
        // la position où la fenêtre se retrouve centrée sur l'écran.
        if (high)
        {
            hi += delta;
            int s = fromCenter ? Nearest(snap, workHi - hi) : Nearest(snap, workHi - hi, workLo + workHi - lo - hi);
            hi += s;
            if (fromCenter) lo -= delta + s;
        }
        else
        {
            lo += delta;
            int s = fromCenter ? Nearest(snap, workLo - lo) : Nearest(snap, workLo - lo, workLo + workHi - lo - hi);
            lo += s;
            if (fromCenter) hi -= delta + s;
        }

        if (hi - lo < min)
        {
            if (fromCenter)
            {
                lo = (lo + hi - min) / 2;
                hi = lo + min;
            }
            else if (high) hi = lo + min;
            else lo = hi - min;
        }
        return (lo, hi);
    }

    /// <summary>Le décalage le plus court parmi ceux qui restent sous la distance d'aimantation, sinon 0.</summary>
    private static int Nearest(int snap, params int[] offsets)
    {
        int best = 0;
        int bestDistance = snap + 1;
        foreach (int offset in offsets)
        {
            if (Math.Abs(offset) < bestDistance)
            {
                best = offset;
                bestDistance = Math.Abs(offset);
            }
        }
        return bestDistance <= snap ? best : 0;
    }

    /// <summary>
    /// Bande saisissable le long d'un bord (ou carré dans un coin), avec sa poignée visible
    /// centrée sur le trait du cadre : un carré dans les coins, une pilule au milieu des bords.
    /// </summary>
    private FrameworkElement Handle(Edges edges, Cursor cursor)
    {
        bool left = edges.HasFlag(Edges.Left), right = edges.HasFlag(Edges.Right);
        bool top = edges.HasFlag(Edges.Top), bottom = edges.HasFlag(Edges.Bottom);
        bool corner = (left || right) && (top || bottom);
        bool side = left || right;

        double width = corner ? 12 : side ? 7 : 26;
        double height = corner ? 12 : side ? 26 : 7;
        var grip = new Rectangle
        {
            Width = width,
            Height = height,
            RadiusX = corner ? 3 : 3.5,
            RadiusY = corner ? 3 : 3.5,
            Fill = Brushes.White,
            Stroke = Accent,
            StrokeThickness = 2,
            HorizontalAlignment = left ? HorizontalAlignment.Left : right ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            VerticalAlignment = top ? VerticalAlignment.Top : bottom ? VerticalAlignment.Bottom : VerticalAlignment.Center,
            Margin = new Thickness(left ? FrameLine - width / 2 : 0, top ? FrameLine - height / 2 : 0,
                                   right ? FrameLine - width / 2 : 0, bottom ? FrameLine - height / 2 : 0),
        };

        var hit = new Border
        {
            Background = Brushes.Transparent, // transparent mais saisissable, contrairement à null
            Cursor = cursor,
            HorizontalAlignment = left ? HorizontalAlignment.Left : right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            VerticalAlignment = top ? VerticalAlignment.Top : bottom ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
            Width = side ? (corner ? CornerSize : HitThickness) : double.NaN,
            Height = top || bottom ? (corner ? CornerSize : HitThickness) : double.NaN,
            // Les coins occupent les extrémités de chaque bord.
            Margin = corner ? new Thickness(0)
                : side ? new Thickness(0, CornerSize, 0, CornerSize)
                : new Thickness(CornerSize, 0, CornerSize, 0),
            Child = grip,
        };
        hit.MouseEnter += (_, _) => grip.Fill = Accent;
        hit.MouseLeave += (_, _) => grip.Fill = Brushes.White;
        hit.MouseLeftButtonDown += (_, e) =>
        {
            BeginDrag(edges, hit);
            e.Handled = true;
        };
        return hit;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
