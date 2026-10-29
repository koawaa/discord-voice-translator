using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace DiscordVoiceTranslator.App.Interop;

/// <summary>
/// Raccourcis clavier globaux (RegisterHotKey) : ils fonctionnent même quand le jeu a le
/// focus. Windows ne prévient que nous, le jeu ne reçoit pas la combinaison.
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public GlobalHotkeys(IntPtr hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
    }

    /// <summary>Renvoie false si la combinaison est déjà prise par un autre programme.</summary>
    public bool Register(ModifierKeys modifiers, Key key, Action action)
    {
        int id = _nextId++;
        // Les valeurs de ModifierKeys (Alt=1, Control=2, Shift=4, Windows=8) sont celles de MOD_*.
        if (!RegisterHotKey(_source.Handle, id, (uint)modifiers | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(key)))
            return false;
        _actions[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys)
            UnregisterHotKey(_source.Handle, id);
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
