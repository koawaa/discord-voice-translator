using System.Runtime.InteropServices;
using Drawing = System.Drawing;

namespace DiscordVoiceTranslator.App.Interop;

internal static class NativeWindow
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;   // les clics traversent vers le jeu
    private const long WS_EX_TOOLWINDOW = 0x00000080;    // absent d'Alt+Tab et de la barre des tâches
    private const long WS_EX_LAYERED = 0x00080000;       // transparence par pixel
    private const long WS_EX_NOACTIVATE = 0x08000000;    // ne vole jamais le focus au jeu

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

    /// <summary>
    /// Mode normal : traversable par la souris. Mode édition : on retire seulement
    /// WS_EX_TRANSPARENT pour pouvoir saisir la fenêtre, sans jamais l'activer.
    /// </summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        style = clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>
    /// Certains jeux en fenêtré sans bordure repassent devant les fenêtres « topmost » :
    /// on réaffirme la position en tête de pile, sans activer ni déplacer.
    /// </summary>
    public static void KeepOnTop(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    /// <summary>Position et taille en pixels écran, comme le curseur et les écrans de Windows Forms.</summary>
    public static Drawing.Rectangle GetBounds(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        return Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    /// <summary>Position et taille d'un seul coup : aucun bord ne bouge en deux temps.</summary>
    public static void SetBounds(IntPtr hwnd, Drawing.Rectangle bounds) =>
        SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    /// <summary>
    /// État réel de la touche, même quand le jeu a le focus clavier (l'overlay ne l'a jamais) :
    /// Keyboard.Modifiers ne voit que les touches reçues par notre propre fenêtre.
    /// </summary>
    public static bool IsKeyDown(System.Windows.Input.Key key) =>
        (GetAsyncKeyState(System.Windows.Input.KeyInterop.VirtualKeyFromKey(key)) & 0x8000) != 0;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_CAPTION_COLOR = 35;

    /// <summary>
    /// Barre de titre sombre (Windows 10 20H1+), et de la couleur donnée sous Windows 11.
    /// Sans effet sur les versions plus anciennes : la fenêtre garde sa barre claire.
    /// </summary>
    public static void UseDarkTitleBar(IntPtr hwnd, System.Windows.Media.Color caption)
    {
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int colorRef = caption.R | caption.G << 8 | caption.B << 16;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
}
