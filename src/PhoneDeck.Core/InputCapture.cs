using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneDeck.Core;

/// <summary>
/// Captures local keyboard and mouse input via low-level hooks and translates it to HID reports.
/// Input is swallowed while capturing; Ctrl+Alt+Q releases it.
/// </summary>
public sealed class InputCapture : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;    private const int WH_MOUSE_LL = 14;
    private const int HC_ACTION = 0;
    private const uint LLMHF_INJECTED = 0x01;
    /// <summary>Set for keys that send an E0 prefix: the navigation cluster, Keypad Enter, Keypad /.</summary>
    private const uint LLKHF_EXTENDED = 0x01;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const uint WM_SYNC_MOUSE_HOOK = 0x8000 + 1; // WM_APP + 1
    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04;
    private const int SM_SWAPBUTTON = 23;

    private readonly HashSet<byte> _pressedUsages = [];
    private readonly HashSet<int> _pressedVirtualKeys = [];
    private readonly HashSet<int> _localPressedVirtualKeys = [];
    private LowLevelProc? _keyboardProc;
    private LowLevelProc? _mouseProc;
    private WinEventProc? _winEventProc;
    private IntPtr _keyboardHook, _mouseHook, _winEventHook;
    private Thread? _thread;
    private uint _threadId;
    private MouseButtons _buttons;
    private MouseButtons _localButtons;
    private int _centerX, _centerY;
    private int _keyboardEvents, _mouseEvents, _rearms;
    private bool _switchLatched;
    private bool _capsLockSwitchLatched;
    private bool _recenterPending;
    private volatile bool _passThrough;
    private volatile bool _running;
    private volatile bool _chordSwitchEnabled = true;
    private volatile bool _capsLockSwitchEnabled = true;
    private volatile bool _touchGoesToHost;
    // Swapped from the settings-watcher thread while the hook thread reads it, so the field has
    // to be volatile even though the reference assignment itself is atomic.
    private volatile ShakeGestureDetector? _gesture;
    // While input stays local the hook reports absolute positions, so the previous point is
    // needed to turn them into the deltas the gesture is built on.
    private int _lastLocalX, _lastLocalY;
    private bool _haveLocalPoint;
    private Func<int> _now = () => Environment.TickCount;

    public event Action<KeyModifiers, byte[]>? KeyboardReport;
    public event Action<MouseButtons, int, int, int>? MouseReport;
    public event Action? SwitchHostRequested;
    public event Action? StopRequested;
    public event Action<string>? Log;

    public bool IsRunning => _running;

    public bool PassThrough => _passThrough;

    /// <summary>Lets local input through untouched; re-centres the cursor when redirect resumes
    /// so the first delta is not the distance the pointer travelled locally.</summary>
    public void SetPassThrough(bool value)
    {
        if (_passThrough == value) return;
        _passThrough = value;
        if (!value && _running) SetCursorPos(_centerX, _centerY);
        // A target change invalidates gesture state: the shake that caused it is usually still
        // being performed, and local deltas need a fresh reference point either way.
        _gesture?.Reset();
        _haveLocalPoint = false;
        RequestMouseHookSync();
    }

    /// <summary>
    /// Turns the Ctrl+D+C switch on or off. It can leak whichever of D or C is pressed first to
    /// the current target, so callers may prefer Ctrl+Alt+CapsLock alone.
    /// </summary>
    public void SetChordSwitch(bool enabled) => _chordSwitchEnabled = enabled;

    /// <summary>
    /// Turns the Ctrl+Alt+CapsLock switch on or off. When off, the combination is an ordinary
    /// Caps Lock press with modifiers, sent wherever input is going.
    /// </summary>
    public void SetCapsLockSwitch(bool enabled) => _capsLockSwitchEnabled = enabled;

    /// <summary>
    /// When on, touches on the video are for the host, so the mouse events Windows makes from
    /// them are dropped instead of clicking whatever is showing the video.
    /// </summary>
    public void SetTouchGoesToHost(bool enabled) => _touchGoesToHost = enabled;

    /// <summary>
    /// A low-level mouse hook delays every pointer event system-wide, so it is only installed
    /// while input is redirected or the shake gesture needs local motion.
    /// </summary>
    internal static bool NeedsMouseHook(bool passThrough, bool gestureEnabled) => !passThrough || gestureEnabled;

    /// <summary>
    /// Windows also reports touch and pen as mouse events, tagged with this signature. Those
    /// belong to the touch layer; forwarding them would fling the phone's pointer around.
    /// </summary>
    internal static bool IsTouchPromoted(IntPtr extraInfo) => ((long)extraInfo & 0xFFFFFF00L) == 0xFF515700L;

    // Hooks belong to the thread that installs them, so changes are marshalled to the hook thread.
    private void RequestMouseHookSync()
    {
        if (_running && _threadId != 0)
            PostThreadMessage(_threadId, WM_SYNC_MOUSE_HOOK, IntPtr.Zero, IntPtr.Zero);
    }

    private void SyncMouseHook()
    {
        var needed = NeedsMouseHook(_passThrough, _gesture is not null);
        if (needed == (_mouseHook != IntPtr.Zero)) return;

        if (needed)
        {
            SeedLocalButtons();
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc!, GetModuleHandle(null), 0);
            return;
        }

        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        // Button-up events are no longer visible, so nothing can be left believed held.
        _buttons = MouseButtons.None;
    }

    /// <summary>
    /// Buttons went unobserved while unhooked. One held now was pressed locally, so its release
    /// has to stay local rather than being swallowed and leaving Windows with it stuck down.
    /// </summary>
    private void SeedLocalButtons()
    {
        var swapped = GetSystemMetrics(SM_SWAPBUTTON) != 0;
        var held = MouseButtons.None;
        if (IsPhysicallyDown(VK_LBUTTON)) held |= swapped ? MouseButtons.Right : MouseButtons.Left;
        if (IsPhysicallyDown(VK_RBUTTON)) held |= swapped ? MouseButtons.Left : MouseButtons.Right;
        if (IsPhysicallyDown(VK_MBUTTON)) held |= MouseButtons.Middle;
        _localButtons = held;
    }

    private static bool IsPhysicallyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// Enables or disables the pointer switch gesture. When enabled, shaking the pointer back
    /// and forth cycles the target the same way the host-switch hotkey does. Pass null to disable.
    /// </summary>
    public void SetSwitchGesture(SwitchGestureOptions? options)
    {
        _gesture = options is { Enabled: true } ? new ShakeGestureDetector(options) : null;
        _haveLocalPoint = false;
        RequestMouseHookSync();
    }

    /// <summary>Test hook: replaces the wall clock used by the switch gesture.</summary>
    internal void SetNow(Func<int> now) => _now = now;

    /// <summary>Logs every hook event and report; useful only for diagnosing delivery problems.</summary>
    public bool Verbose { get; init; }

    public int KeyboardEvents => _keyboardEvents;
    public int MouseEvents => _mouseEvents;
    public int Rearms => _rearms;

    public void Start()
    {
        if (_running) return;
        _running = true;

        // The hook reports physical pixels, but GetSystemMetrics/SetCursorPos are virtualised for a
        // DPI-unaware process. On a scaled display that mismatch adds a constant offset to every
        // delta and walks the remote pointer into a corner.
        if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_ACCESS_DENIED just means awareness was already set, which is fine.
            if (error != 5) Log?.Invoke($"  [hook] could not set DPI awareness (err {error}); pointer may drift on scaled displays");
        }

        _thread = new Thread(HookThread) { IsBackground = true, Name = "PhoneDeck input capture" };
        // Must not be STA: an STA hook thread dispatches WinRT completions through the same
        // message pump the input callbacks saturate, which stalls GATT notifications for seconds.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();

        // Default timer granularity is ~15.6 ms, too coarse to pace HID reports.
        timeBeginPeriod(1);

        _centerX = GetSystemMetrics(0) / 2;
        _centerY = GetSystemMetrics(1) / 2;
        if (!_passThrough) SetCursorPos(_centerX, _centerY);

        _keyboardProc = KeyboardHookProc;
        _mouseProc = MouseHookProc;

        var measureLatency = Environment.GetEnvironmentVariable("BLEHID_INPUT_TIMING") == "1";
        var localKeyboardTiming = new HookTiming();
        var remoteKeyboardTiming = new HookTiming();
        var localMouseTiming = new HookTiming();
        var remoteMouseTiming = new HookTiming();
        if (measureLatency)
        {
            _keyboardProc = MeasureHook(_keyboardProc,
                Marshal.OffsetOf<KBDLLHOOKSTRUCT>(nameof(KBDLLHOOKSTRUCT.time)).ToInt32(),
                localKeyboardTiming, remoteKeyboardTiming);
            _mouseProc = MeasureHook(_mouseProc,
                Marshal.OffsetOf<MSLLHOOKSTRUCT>(nameof(MSLLHOOKSTRUCT.time)).ToInt32(),
                localMouseTiming, remoteMouseTiming);
        }

        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        var keyboardError = Marshal.GetLastWin32Error();
        SyncMouseHook();
        var mouseError = Marshal.GetLastWin32Error();

        Log?.Invoke($"  [hook] keyboard=0x{_keyboardHook:x} (err {keyboardError}), mouse=0x{_mouseHook:x} (err {mouseError}), screen={GetSystemMetrics(0)}x{GetSystemMetrics(1)}, center={_centerX},{_centerY}");

        // Hook chains run newest-first, so apps that grab input (Windows App / mstsc, some games)
        // win simply by hooking after us. Re-installing on focus change puts us back in front.
        _winEventProc = ForegroundChanged;
        _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        int result;
        while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
        {
            if (message.message == WM_SYNC_MOUSE_HOOK)
            {
                SyncMouseHook();
                continue;
            }

            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        if (Verbose)
            Log?.Invoke($"  [hook] message loop exited ({result}), keyboard events={_keyboardEvents}, mouse events={_mouseEvents}, rearms={_rearms}");

        if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = _winEventHook = IntPtr.Zero;
        timeEndPeriod(1);

        if (measureLatency)
        {
            var summary = new[]
            {
                $"local keyboard: {localKeyboardTiming}",
                $"local mouse: {localMouseTiming}",
                $"remote keyboard: {remoteKeyboardTiming}",
                $"remote mouse: {remoteMouseTiming}"
            };
            foreach (var line in summary) Log?.Invoke($"  [input timing] {line}");
            try
            {
                var path = AppPaths.InLogs($"input-timing-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.log");
                File.WriteAllLines(path, summary);
                Log?.Invoke($"  [input timing] saved to {path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log?.Invoke($"  [input timing] could not save summary: {ex.Message}");
            }
        }
    }

    private LowLevelProc MeasureHook(LowLevelProc callback, int timestampOffset,
        HookTiming localTiming, HookTiming remoteTiming) => (code, wParam, lParam) =>
    {
        if (code != HC_ACTION) return callback(code, wParam, lParam);

        var timing = _passThrough ? localTiming : remoteTiming;
        var observedTime = Environment.TickCount;
        var eventTime = Marshal.ReadInt32(lParam, timestampOffset);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return callback(code, wParam, lParam);
        }
        finally
        {
            timing.Record(eventTime, observedTime, Stopwatch.GetTimestamp() - started);
        }
    };

    internal sealed class HookTiming
    {
        public long Count { get; private set; }
        public long TotalEventAgeMs { get; private set; }
        public int MaxEventAgeMs { get; private set; }
        public long TotalCallbackTicks { get; private set; }
        public long MaxCallbackTicks { get; private set; }

        public void Record(int eventTime, int observedTime, long callbackTicks)
        {
            var eventAgeMs = Math.Max(0, unchecked(observedTime - eventTime));
            Count++;
            TotalEventAgeMs += eventAgeMs;
            MaxEventAgeMs = Math.Max(MaxEventAgeMs, eventAgeMs);
            TotalCallbackTicks += callbackTicks;
            MaxCallbackTicks = Math.Max(MaxCallbackTicks, callbackTicks);
        }

        public override string ToString()
        {
            var divisor = Math.Max(1, Count);
            var averageAgeMs = (double)TotalEventAgeMs / divisor;
            var averageCallbackMs = TotalCallbackTicks * 1000.0 / Stopwatch.Frequency / divisor;
            var maxCallbackMs = MaxCallbackTicks * 1000.0 / Stopwatch.Frequency;
            return FormattableString.Invariant($"events={Count}, event age avg/max={averageAgeMs:F2}/{MaxEventAgeMs} ms, hook+chain avg/max={averageCallbackMs:F3}/{maxCallbackMs:F3} ms");
        }
    }

    private void ForegroundChanged(IntPtr hook, uint eventType, IntPtr window, int idObject, int idChild, uint thread, uint time)
    {
        if (!_running || idObject != 0 /* OBJID_WINDOW */) return;

        var module = GetModuleHandle(null);
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc!, module, 0);
        // Only re-armed if installed: it is deliberately absent while input stays local.
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc!, module, 0);
        }
        _rearms++;

        if (Verbose)
            Log?.Invoke($"  [hook] re-armed after focus change (keyboard=0x{_keyboardHook:x}, mouse=0x{_mouseHook:x}, rearms={_rearms})");
    }

    private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != HC_ACTION) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var message = (int)wParam;
        var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var virtualKey = (int)data.vkCode;
        return HandleKeyboardEvent(virtualKey, isDown, data.scanCode, (data.flags & LLKHF_EXTENDED) != 0)
            ? (IntPtr)1
            : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>
    /// Scan code and the extended flag identify the physical key, which the virtual-key code
    /// alone cannot: Windows has already applied the local Num Lock state by this point.
    /// </summary>
    internal bool HandleKeyboardEvent(int virtualKey, bool isDown, uint scanCode = 0, bool extended = false)
    {
        _keyboardEvents++;
        var releaseLocally = !isDown && _localPressedVirtualKeys.Remove(virtualKey);

        if (isDown) _pressedVirtualKeys.Add(virtualKey);
        else _pressedVirtualKeys.Remove(virtualKey);

        if (virtualKey == 0x14 && _capsLockSwitchEnabled)
        {
            if (_capsLockSwitchLatched)
            {
                if (!isDown) _capsLockSwitchLatched = false;
                return !releaseLocally;
            }

            if (isDown && IsDown(0x11) && IsDown(0x12))
            {
                _capsLockSwitchLatched = true;
                RequestHostSwitch();
                return true;
            }
        }

        if (isDown && virtualKey == 0x51 && IsDown(0x11) && IsDown(0x12)) // Ctrl+Alt+Q
        {
            _pressedVirtualKeys.Clear();
            ReleaseHeldInput();
            StopRequested?.Invoke();
            return true;
        }

        // Ctrl+D+C switches host. Latched so key auto-repeat cannot cycle past the target.
        if (!isDown && virtualKey is 0x44 or 0x43) _switchLatched = false;
        if (_chordSwitchEnabled && isDown && IsDown(0x11) &&
            _pressedVirtualKeys.Contains(0x44) && _pressedVirtualKeys.Contains(0x43))
        {
            if (!_switchLatched)
            {
                _switchLatched = true;
                RequestHostSwitch();
            }
            return true;
        }

        // Hotkeys stay live in pass-through, but nothing else is captured or swallowed.
        if (_passThrough)
        {
            if (isDown) _localPressedVirtualKeys.Add(virtualKey);
            return false;
        }

        if (VirtualKeyMap.TryGetUsage(virtualKey, scanCode, extended, out var usage))
        {
            if (isDown) _pressedUsages.Add(usage);
            else _pressedUsages.Remove(usage);
        }

        var modifiers = CurrentModifiers();
        var usages = _pressedUsages.Take(6).ToArray();
        if (Verbose && _keyboardEvents <= 20)
            // Timing and counts only: key codes in a log are a record of what was typed.
            Log?.Invoke($"  [key] {(isDown ? "down" : "up")} -> {usages.Length} key(s) held");

        KeyboardReport?.Invoke(modifiers, usages);
        return !releaseLocally;
    }

    private void RequestHostSwitch()
    {
        ReleaseHeldInput();
        SwitchHostRequested?.Invoke();
    }

    /// <summary>
    /// Releases everything held before input leaves a host. These reports are queued ahead of the
    /// target change, so the host sees the release instead of being left with a key or a mouse
    /// button stuck down.
    /// </summary>
    private void ReleaseHeldInput()
    {
        _pressedUsages.Clear();
        KeyboardReport?.Invoke(KeyModifiers.None, []);
        if (_buttons != MouseButtons.None)
        {
            _buttons = MouseButtons.None;
            MouseReport?.Invoke(_buttons, 0, 0, 0);
        }
    }

    /// <summary>
    /// Local side of the switch gesture: the hook reports absolute positions while input stays
    /// on this PC, so they are differenced into the deltas the recogniser expects.
    /// </summary>
    private void TrackLocalGesture(int x, int y)
    {
        if (_gesture is null) return;

        // The first move after a target change has no predecessor; differencing against a stale
        // point would manufacture one huge stroke.
        if (!_haveLocalPoint)
        {
            _lastLocalX = x;
            _lastLocalY = y;
            _haveLocalPoint = true;
            return;
        }

        var dx = x - _lastLocalX;
        var dy = y - _lastLocalY;
        _lastLocalX = x;
        _lastLocalY = y;
        TrackGesture(dx, dy);
    }

    /// <summary>
    /// Feeds one pointer delta to the switch gesture. Deltas are available whichever way input
    /// is flowing, so the same gesture works to hand input to a host and to take it back.
    /// </summary>
    internal void TrackGesture(int dx, int dy)
    {
        // Read once: the settings watcher can swap the detector out between the two uses.
        var gesture = _gesture;
        if (gesture is null || !gesture.Feed(dx, dy, _now())) return;
        if (Verbose) Log?.Invoke("  [gesture] shake detected - switching target");
        RequestHostSwitch();
    }

    internal bool HandleMouseButtonEvent(MouseButtons button, bool isDown)
    {
        var releaseLocally = !isDown && (_localButtons & button) != MouseButtons.None;
        if (!isDown) _localButtons &= ~button;

        if (_passThrough)
        {
            if (isDown) _localButtons |= button;
            return false;
        }

        _mouseEvents++;
        if (isDown) _buttons |= button;
        else _buttons &= ~button;
        MouseReport?.Invoke(_buttons, 0, 0, 0);
        return !releaseLocally;
    }

    private IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != HC_ACTION) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if ((data.flags & LLMHF_INJECTED) != 0)
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        if (IsTouchPromoted(data.dwExtraInfo))
        {
            // Outside the video it stays a normal tap, which is how the button bar gets its clicks.
            if (_touchGoesToHost && !_passThrough && TouchMapper.IsInsideVideo(data.pt.x, data.pt.y,
                    GetSystemMetrics(0), GetSystemMetrics(1), TouchMapper.DefaultVideoAspect))
                return 1;

            // It moves the real cursor off centre, so the next real delta would be a huge jump.
            _recenterPending = !_passThrough;
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        var message = (int)wParam;
        var button = message switch
        {
            WM_LBUTTONDOWN or WM_LBUTTONUP => MouseButtons.Left,
            WM_RBUTTONDOWN or WM_RBUTTONUP => MouseButtons.Right,
            WM_MBUTTONDOWN or WM_MBUTTONUP => MouseButtons.Middle,
            _ => MouseButtons.None
        };
        if (button != MouseButtons.None)
        {
            var isDown = message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN;
            return HandleMouseButtonEvent(button, isDown)
                ? (IntPtr)1
                : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        if (_passThrough)
        {
            // Input stays local, but pointer motion still feeds the switch gesture so a shake
            // can hand input to a remote host.
            if ((int)wParam == WM_MOUSEMOVE) TrackLocalGesture(data.pt.x, data.pt.y);
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        _mouseEvents++;

        if (_recenterPending && (int)wParam == WM_MOUSEMOVE)
        {
            _recenterPending = false;
            SetCursorPos(_centerX, _centerY);
            return 1;
        }

        switch ((int)wParam)
        {
            case WM_MOUSEMOVE:
                var dx = data.pt.x - _centerX;
                var dy = data.pt.y - _centerY;
                if (dx != 0 || dy != 0)
                {
                    if (Verbose && _mouseEvents <= 10) Log?.Invoke($"  [mouse] move {dx},{dy}");
                    MouseReport?.Invoke(_buttons, dx, dy, 0);
                    SetCursorPos(_centerX, _centerY);
                    TrackGesture(dx, dy);
                }
                break;

            case WM_MOUSEWHEEL:
                var notches = (short)((data.mouseData >> 16) & 0xFFFF) / 120;
                if (notches != 0) MouseReport?.Invoke(_buttons, 0, 0, notches);
                break;
        }

        return 1; // swallow locally
    }

    private bool IsDown(int virtualKey) => virtualKey switch
    {
        0x11 => _pressedVirtualKeys.Contains(0xA2) || _pressedVirtualKeys.Contains(0xA3),
        0x12 => _pressedVirtualKeys.Contains(0xA4) || _pressedVirtualKeys.Contains(0xA5),
        _ => _pressedVirtualKeys.Contains(virtualKey)
    };

    private KeyModifiers CurrentModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (_pressedVirtualKeys.Contains(0xA0)) modifiers |= KeyModifiers.LeftShift;
        if (_pressedVirtualKeys.Contains(0xA1)) modifiers |= KeyModifiers.RightShift;
        if (_pressedVirtualKeys.Contains(0xA2)) modifiers |= KeyModifiers.LeftControl;
        if (_pressedVirtualKeys.Contains(0xA3)) modifiers |= KeyModifiers.RightControl;
        if (_pressedVirtualKeys.Contains(0xA4)) modifiers |= KeyModifiers.LeftAlt;
        if (_pressedVirtualKeys.Contains(0xA5)) modifiers |= KeyModifiers.RightAlt;
        if (_pressedVirtualKeys.Contains(0x5B)) modifiers |= KeyModifiers.LeftGui;
        if (_pressedVirtualKeys.Contains(0x5C)) modifiers |= KeyModifiers.RightGui;
        return modifiers;
    }

    public void Dispose() => Stop();

    private delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
