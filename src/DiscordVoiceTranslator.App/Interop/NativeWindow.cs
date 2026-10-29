using System.Runtime.InteropServices;

namespace DiscordVoiceTranslator.App.Interop;

internal static class NativeWindow
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;   // les clics traversent vers le jeu
    private const long WS_EX_TOOLWINDOW = 0x00000080;    // absent d'Alt+Tab et de la barre des tâches
    private const long WS_EX_LAYERED = 0x00080000;       // transparence par pixel
    private const long WS_EX_NOACTIVATE = 0x08000000;    // ne vole jamais le focus au jeu

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
