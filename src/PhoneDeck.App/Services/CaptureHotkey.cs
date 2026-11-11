using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PhoneDeck.App.Services;

/// <summary>
/// Ctrl+Alt+Q as an ordinary Windows hotkey, so it can turn capture on. While capture runs the
/// keyboard hook sees the keys first and swallows them to turn capture off, so this only ever
/// fires while capture is off. Windows reports just this one combination, not other typing.
/// </summary>
public sealed class CaptureHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_Q = 0x51;
    private const int HotkeyId = 0xB1E;

    private readonly Action _pressed;
    private readonly HwndSource _window;
    private readonly bool _registered;

    public CaptureHotkey(Action pressed, Action<string> log)
    {
        _pressed = pressed;

        // A message-only window: it receives WM_HOTKEY but is never shown.
        _window = new HwndSource(new HwndSourceParameters("PhoneDeckCaptureHotkey")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0
        });
        _window.AddHook(WndProc);

        // No-repeat, so a key still held from turning capture off cannot turn it straight back on.
        _registered = RegisterHotKey(_window.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_Q);
        log(_registered
            ? "Ctrl+Alt+Q turns capture on and off"
            : $"Ctrl+Alt+Q could not be registered, so it only turns capture off (err {Marshal.GetLastWin32Error()}); another app may be using it");
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
        if (_registered) UnregisterHotKey(_window.Handle, HotkeyId);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
