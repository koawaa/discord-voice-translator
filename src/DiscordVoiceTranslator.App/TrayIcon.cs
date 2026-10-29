using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using static DiscordVoiceTranslator.Core.Localization.UiText;
using Forms = System.Windows.Forms;

namespace DiscordVoiceTranslator.App;

/// <summary>
/// Icône dans la zone de notification (derrière la flèche des icônes cachées) : clic droit
/// pour le menu, double-clic pour le mode édition.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Icon _icon = DrawIcon();
    private readonly Forms.ToolStripMenuItem _pauseItem;

    public event Action? EditModeRequested;
    public event Action? VisibilityToggleRequested;
    public event Action? PauseToggleRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        _menu.Items.Add(T("Edit mode (Ctrl+Alt+F9)", "Mode édition (Ctrl+Alt+F9)"), null, (_, _) => EditModeRequested?.Invoke());
        _menu.Items.Add(T("Show / hide (Ctrl+Alt+F10)", "Afficher / masquer (Ctrl+Alt+F10)"), null, (_, _) => VisibilityToggleRequested?.Invoke());
        _pauseItem = new Forms.ToolStripMenuItem(PauseText(false), null, (_, _) => PauseToggleRequested?.Invoke());
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(T("Settings...", "Paramètres..."), null, (_, _) => SettingsRequested?.Invoke());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(T("Quit", "Quitter"), null, (_, _) => QuitRequested?.Invoke());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = T("Discord Voice Translator", "Traducteur vocal Discord"),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => EditModeRequested?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        _pauseItem.Text = PauseText(paused);
        _notifyIcon.Text = T("Discord Voice Translator", "Traducteur vocal Discord") + (paused ? T(" (paused)", " (en pause)") : "");
    }

    private static string PauseText(bool paused) =>
        paused ? T("Resume (Ctrl+Alt+F11)", "Reprendre (Ctrl+Alt+F11)") : T("Pause (Ctrl+Alt+F11)", "Mettre en pause (Ctrl+Alt+F11)");

    // Une bulle de dialogue avec deux lignes de « sous-titres », lisible même en 16 px.
    private static Icon DrawIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var bubble = new GraphicsPath { FillMode = FillMode.Winding })
        using (var blue = new SolidBrush(Color.FromArgb(0x4F, 0xA3, 0xFF)))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            const int r = 12;
            bubble.AddArc(1, 2, r, r, 180, 90);
            bubble.AddArc(31 - r, 2, r, r, 270, 90);
            bubble.AddArc(31 - r, 24 - r, r, r, 0, 90);
            bubble.AddArc(1, 24 - r, r, r, 90, 90);
            bubble.CloseFigure();
            bubble.AddPolygon(new[] { new Point(7, 21), new Point(15, 21), new Point(6, 30) });
            graphics.FillPath(blue, bubble);
            graphics.FillRectangle(Brushes.White, 7, 8, 18, 4);
            graphics.FillRectangle(Brushes.White, 7, 15, 12, 4);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle ne possède pas le handle : on clone puis on le libère nous-mêmes.
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        // Sans ça, l'icône reste affichée après la fermeture jusqu'au survol de la souris.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
