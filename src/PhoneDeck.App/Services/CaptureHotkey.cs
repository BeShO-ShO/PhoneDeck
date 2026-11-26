using System.Runtime.InteropServices;
using System.Windows.Interop;
using PhoneDeck.Core;

namespace PhoneDeck.App.Services;

/// <summary>
/// The capture hotkey (Ctrl+Alt+Q unless changed in Settings) as an ordinary Windows hotkey, so
/// it can turn capture on. While capture runs the keyboard hook sees the keys first and swallows
/// them to turn capture off, so this only ever fires while capture is off. Windows reports just
/// this one combination, not other typing.
/// </summary>
public sealed class CaptureHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HotkeyId = 0xB1E;

    private readonly Action _pressed;
    private readonly Action<string> _log;
    private readonly HwndSource _window;
    private bool _registered;
    private Hotkey _hotkey;

    public CaptureHotkey(Hotkey hotkey, Action pressed, Action<string> log)
    {
        _pressed = pressed;
        _log = log;

        // A message-only window: it receives WM_HOTKEY but is never shown.
        _window = new HwndSource(new HwndSourceParameters("PhoneDeckCaptureHotkey")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0
        });
        _window.AddHook(WndProc);
        Change(hotkey);
    }

    /// <summary>Registers a different combination in place of the current one.</summary>
    public void Change(Hotkey hotkey)
    {
        Suspend();
        _hotkey = hotkey;
        Resume();
    }

    /// <summary>Lets Windows deliver the combination to other apps, e.g. while Settings records a new one.</summary>
    public void Suspend()
    {
        if (!_registered) return;
        UnregisterHotKey(_window.Handle, HotkeyId);
        _registered = false;
    }

    public void Resume()
    {
        if (_registered) return;
        // No-repeat, so a key still held from turning capture off cannot turn it straight back on.
        _registered = RegisterHotKey(_window.Handle, HotkeyId, (uint)_hotkey.Modifiers | MOD_NOREPEAT, (uint)_hotkey.VirtualKey);
        _log(_registered
            ? $"{_hotkey} turns capture on and off"
            : $"{_hotkey} could not be registered, so it only turns capture off (err {Marshal.GetLastWin32Error()}); another app may be using it");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Suspend();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
