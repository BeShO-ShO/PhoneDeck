using System.Collections.Concurrent;
using PhoneDeck.Core;
using static PhoneDeck.App.Viewer.ViewerNative;

namespace PhoneDeck.App.Viewer;

/// <summary>
/// PhoneDeck's own window for the capture device, in place of mpv. A plain Win32 window on a
/// thread of its own, so a busy settings window can never hold up the picture, and so DirectX
/// can draw straight into it (WPF can't share its window). It knows exactly where it draws the
/// picture, so it keeps <see cref="VideoRegion"/> up to date itself.
/// <list type="bullet">
/// <item>Opens full screen on the main display. Double-click, F11 or F switch to a window and
/// back, Esc leaves full screen. While input goes to the phone, a double tap is the phone's.</item>
/// <item>While input goes to the phone the pointer is parked over the picture, so it is hidden
/// there. Otherwise it hides after a second without moving, as in mpv.</item>
/// <item>Closing the window closes the viewer; the next capture opens it again.</item>
/// </list>
/// </summary>
internal sealed class CaptureViewer : IDisposable
{
    public const string Title = "PhoneDeck - phone screen";
    private const string ClassName = "PhoneDeckCaptureViewer";
    private const uint WmApplyCursor = WM_APP + 1;
    private const uint WmToggleFullScreen = WM_APP + 2;
    private const uint WmVideoSizeChanged = WM_APP + 3;
    private const uint WmActivate = WM_APP + 4;
    private static readonly IntPtr IdleTimer = 1;
    private const int IdleHideMs = 1000;

    // One window procedure for every viewer window; kept in a static so it is never collected.
    private static readonly WndProc Procedure = WindowProcedure;
    private static readonly ConcurrentDictionary<IntPtr, CaptureViewer> Windows = new();
    private static readonly object RegisterGate = new();
    private static bool _registered;
    [ThreadStatic] private static CaptureViewer? _creating;

    private readonly Action<string> _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _created = new();
    private VideoPipeline? _video;
    // The cast's picture instead of _video; made and used on the window thread.
    private readonly bool _castMode;
    private CastPicture? _cast;
    private bool _windowFailed;
    private SoundPassthrough? _sound;
    private readonly object _soundGate = new();
    private bool _disposed;

    private IntPtr _window;
    private volatile bool _capturing;
    private volatile bool _closed;
    private volatile int _clientWidth, _clientHeight;
    private readonly object _aspectGate = new();
    private double _videoAspect = TouchMapper.DefaultVideoAspect;

    // Used only on the window thread.
    private bool _fullScreen;
    private RECT _windowedRect;
    private long _lastMove = Environment.TickCount64;
    private POINT _lastMovePoint;
    private bool _idleHidden;

    /// <param name="audioDevice">The capture device's sound input, or null for no sound.</param>
    public CaptureViewer(string videoDevice, string? audioDevice, Action<string> log) : this(log, cast: false)
    {
        if (_windowFailed) return;

        _video = new VideoPipeline(videoDevice, _window, () => (_clientWidth, _clientHeight), OnVideoSize, _log);

        if (audioDevice is not null)
        {
            // Off the UI thread: Windows' audio objects belong to the thread that makes them,
            // and the thread pool's threads are the multithreaded kind they work best on.
            _ = Task.Run(() =>
            {
                var sound = SoundPassthrough.TryStart(audioDevice, _log);
                lock (_soundGate)
                {
                    if (_disposed) sound?.Dispose();
                    else _sound = sound;
                }
            });
        }
    }

    private CaptureViewer(Action<string> log, bool cast)
    {
        _log = log;
        _castMode = cast;
        _thread = new Thread(RunWindow) { IsBackground = true, Name = "PhoneDeck viewer window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_created.Wait(TimeSpan.FromSeconds(5)) || _window == IntPtr.Zero)
        {
            _closed = true;
            _windowFailed = true;
            _log("viewer: the window could not be created");
        }
    }

    /// <summary>
    /// The same window for PhoneDeck's own cast receiver: black until <see cref="ShowCast"/>
    /// gives it the cast's player. Sound comes from the player itself.
    /// </summary>
    public static CaptureViewer ForCast(Action<string> log) => new(log, cast: true);

    /// <summary>
    /// Shows the cast's player, or black for null (waiting for the phone). A retired player is
    /// closed once the window has let go of it.
    /// </summary>
    public void ShowCast(global::Windows.Media.Playback.MediaPlayer? player,
        global::Windows.Media.Playback.MediaPlayer? retired = null)
    {
        if (_cast is { } cast) cast.Show(player, retired);
        else CloseLater(retired);
    }

    private void CloseLater(global::Windows.Media.Playback.MediaPlayer? retired)
    {
        if (retired is null) return;
        _ = Task.Run(() =>
        {
            try { retired.Dispose(); }
            catch (Exception ex) { _log($"cast: closing the old player: {ex.Message}"); }
        });
    }

    /// <summary>The picture's size, for letterboxing and for where touch lands.</summary>
    public void SetVideoSize(int width, int height)
    {
        OnVideoSize(width, height);
        if (width > 0 && height > 0) _cast?.SetAspect((double)width / height);
    }

    /// <summary>False once the window has been closed, by the user or by <see cref="Dispose"/>.</summary>
    public bool IsOpen => !_closed;

    /// <summary>While input goes to the phone: hide the pointer and leave double taps alone.</summary>
    public void SetCapturing(bool capturing)
    {
        _capturing = capturing;
        Post(WmApplyCursor);
    }

    public void ToggleFullScreen() => Post(WmToggleFullScreen);

    /// <summary>Brings the window to the front and gives it the keyboard.</summary>
    public void Activate() => Post(WmActivate);

    /// <summary>Colour range and picture levels; shown from the next frame, with no added delay.</summary>
    public void SetPicture(PictureRange range, PictureLevels levels) => _video?.SetPicture(range, levels);

    private void Post(uint message)
    {
        if (!_closed && _window != IntPtr.Zero) PostMessage(_window, message, IntPtr.Zero, IntPtr.Zero);
    }

    private void OnVideoSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        lock (_aspectGate) _videoAspect = (double)width / height;
        Post(WmVideoSizeChanged);
    }

    private double VideoAspect
    {
        get { lock (_aspectGate) return _videoAspect; }
    }

    private void RunWindow()
    {
        try
        {
            var instance = GetModuleHandle(null);
            Register(instance);
            // Composition needs a dispatcher queue on this thread before anything is made.
            if (_castMode) CastPicture.EnsureDispatcherQueue();

            var primary = MonitorInfo(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY));
            var screen = primary.rcMonitor;
            _windowedRect = SavedWindowedRect() ?? DefaultWindowedRect(primary.rcWork);

            _creating = this;
            var window = CreateWindowEx(0, ClassName, Title, WS_POPUP | WS_VISIBLE,
                screen.Left, screen.Top, screen.Width, screen.Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            _creating = null;

            if (window == IntPtr.Zero)
            {
                _created.Set();
                return;
            }

            _window = window;
            _fullScreen = true;
            if (_castMode && GetClientRect(window, out var client))
                _cast = new CastPicture(window, client.Width, client.Height, _log);
            SetTimer(window, IdleTimer, 250, IntPtr.Zero);
            SetForegroundWindow(window);
            UpdateRegion();
            _created.Set();

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            _log($"viewer: window failed: {ex.Message}");
        }
        finally
        {
            _closed = true;
            _created.Set();
        }
    }

    private static void Register(IntPtr instance)
    {
        lock (RegisterGate)
        {
            if (_registered) return;
            var windowClass = new WNDCLASSEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_DBLCLKS,
                lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(Procedure),
                hInstance = instance,
                // The exe's own icon: .NET puts ApplicationIcon in as resource 32512.
                hIcon = LoadIcon(instance, 32512),
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = GetStockObject(BLACK_BRUSH),
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref windowClass) == 0)
                throw new InvalidOperationException($"RegisterClassEx failed ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            _registered = true;
        }
    }

    /// <summary>A 16:9 window two thirds the size of the work area, centred.</summary>
    private static RECT DefaultWindowedRect(RECT work)
    {
        var width = work.Width * 2 / 3;
        var height = width * 9 / 16;
        if (height > work.Height * 2 / 3)
        {
            height = work.Height * 2 / 3;
            width = height * 16 / 9;
        }
        var left = work.Left + (work.Width - width) / 2;
        var top = work.Top + (work.Height - height) / 2;
        return new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    /// <summary>Where the window last was when not full screen, if that is still on a screen.</summary>
    private static RECT? SavedWindowedRect()
    {
        if (WindowPlacement.Parse(Services.AppSettings.Instance.ViewerWindow) is not { } saved) return null;
        if (!saved.TitleIsOnScreen((x, y) => MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONULL) != IntPtr.Zero))
            return null;
        return new RECT { Left = saved.Left, Top = saved.Top, Right = saved.Left + saved.Width, Bottom = saved.Top + saved.Height };
    }

    /// <summary>Remembers the window's size and place for next time; saved on the UI thread.</summary>
    private void RememberWindowedRect(RECT rect)
    {
        var text = new WindowPlacement(rect.Left, rect.Top, rect.Width, rect.Height).ToString();
        if (WindowPlacement.Parse(text) is null) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Services.AppSettings.Instance.ViewerWindow = text);
    }

    private static IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!Windows.TryGetValue(window, out var viewer))
        {
            // The first messages arrive inside CreateWindowEx, before it returns the handle.
            if (_creating is not { } creating) return DefWindowProc(window, message, wParam, lParam);
            Windows[window] = creating;
            viewer = creating;
        }

        try
        {
            return viewer.Handle(window, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            // An exception must never unwind into Windows' own code.
            viewer._log($"viewer: {ex.Message}");
            return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private IntPtr Handle(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_SETCURSOR:
                if (LowWord(lParam) == HTCLIENT && PointerHidden)
                {
                    SetCursor(IntPtr.Zero);
                    return 1;
                }
                break;

            case WM_MOUSEMOVE:
                OnMouseMove(lParam);
                break;

            case WM_TIMER:
                if (wParam == IdleTimer) HideIfIdle(window);
                return 0;

            case WmApplyCursor:
                _idleHidden = false;
                _lastMove = Environment.TickCount64;
                if (PointerIsOver(window)) SetCursor(PointerHidden ? IntPtr.Zero : LoadCursor(IntPtr.Zero, IDC_ARROW));
                return 0;

            case WM_SIZE:
                _clientWidth = LowWord(lParam);
                _clientHeight = HighWord(lParam);
                _cast?.Resize(_clientWidth, _clientHeight);
                UpdateRegion();
                break;

            case WM_EXITSIZEMOVE:
                // The end of a drag or resize of the window (not of full screen).
                if (!_fullScreen && !IsIconic(window) && GetWindowRect(window, out var moved))
                {
                    _windowedRect = moved;
                    RememberWindowedRect(moved);
                }
                break;

            case WM_MOVE:
            case WmVideoSizeChanged:
                UpdateRegion();
                break;

            case WM_LBUTTONDOWN:
                // A click or touch on the picture can hand input to the phone. In the letterbox
                // bars or with the setting off it is just a click.
                if (!_capturing && PictureClicked is { } clicked && ClickIsOnPicture(window, lParam)) clicked();
                break;

            case WM_LBUTTONDBLCLK:
                // While input goes to the phone a double tap belongs to it. So does the second
                // click of one that has just handed it over; the black bars still switch.
                if (!_capturing && !(PictureClicked is not null && ClickIsOnPicture(window, lParam)))
                    SetFullScreen(!_fullScreen);
                return 0;

            case WM_KEYDOWN:
                // While input goes to the phone, the keyboard hook keeps keys from reaching here.
                var key = (int)wParam;
                if (key is VK_F11 or VK_F) SetFullScreen(!_fullScreen);
                else if (key == VK_ESCAPE && _fullScreen) SetFullScreen(false);
                return 0;

            case WmToggleFullScreen:
                SetFullScreen(!_fullScreen);
                return 0;

            case WmActivate:
                SetForegroundWindow(window);
                return 0;

            case WM_DISPLAYCHANGE:
                if (_fullScreen) FitToMonitor();
                break;

            case WM_DPICHANGED:
                if (!_fullScreen)
                {
                    var suggested = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(lParam);
                    SetWindowPos(window, IntPtr.Zero, suggested.Left, suggested.Top, suggested.Width, suggested.Height,
                        SWP_NOZORDER | SWP_NOACTIVATE);
                }
                return 0;

            case WM_CLOSE:
                DestroyWindow(window);
                return 0;

            case WM_DESTROY:
                KillTimer(window, IdleTimer);
                Windows.TryRemove(window, out _);
                _closed = true;
                VideoRegion.Current = null;
                // Composition objects belong to this thread.
                _cast?.Dispose();
                _cast = null;
                // Closed by the user: let go of the capture device and the sound now.
                _ = Task.Run(StopMedia);
                PostQuitMessage(0);
                return 0;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    /// <summary>
    /// Called on the window's own thread when the picture is clicked or touched while input stays
    /// on this PC. Null leaves clicks alone.
    /// </summary>
    public Action? PictureClicked { get; set; }

    private static bool ClickIsOnPicture(IntPtr window, IntPtr lParam)
    {
        var point = new POINT { X = (short)LowWord(lParam), Y = (short)HighWord(lParam) };
        if (!ClientToScreen(window, ref point)) return false;
        return VideoRegion.Current is { } picture && picture.Contains(point.X, point.Y);
    }

    private bool PointerHidden => _capturing || _idleHidden;

    private void OnMouseMove(IntPtr lParam)
    {
        // Windows also sends a move when nothing moved (a window appearing under the pointer).
        var point = new POINT { X = (short)LowWord(lParam), Y = (short)HighWord(lParam) };
        if (point.X == _lastMovePoint.X && point.Y == _lastMovePoint.Y) return;
        _lastMovePoint = point;
        _lastMove = Environment.TickCount64;

        if (_idleHidden && !_capturing)
        {
            _idleHidden = false;
            SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
        }
    }

    private void HideIfIdle(IntPtr window)
    {
        if (_capturing || _idleHidden || Environment.TickCount64 - _lastMove < IdleHideMs) return;
        if (!PointerIsOver(window)) return;
        _idleHidden = true;
        SetCursor(IntPtr.Zero);
    }

    private static bool PointerIsOver(IntPtr window) =>
        GetCursorPos(out var point) && WindowFromPoint(point) == window;

    private void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == _fullScreen) return;

        if (fullScreen)
        {
            if (!IsIconic(_window) && GetWindowRect(_window, out var rect))
            {
                _windowedRect = rect;
                RememberWindowedRect(rect);
            }
            SetWindowLongPtr(_window, GWL_STYLE, unchecked((IntPtr)(int)(WS_POPUP | WS_VISIBLE)));
            _fullScreen = true;
            FitToMonitor();
        }
        else
        {
            SetWindowLongPtr(_window, GWL_STYLE, (IntPtr)(int)(WS_OVERLAPPEDWINDOW | WS_VISIBLE));
            _fullScreen = false;
            var rect = _windowedRect;
            SetWindowPos(_window, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
                SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
    }

    /// <summary>Covers the whole display the window is on, taskbar included, as mpv's full screen does.</summary>
    private void FitToMonitor()
    {
        var screen = MonitorInfo(MonitorFromWindow(_window, MONITOR_DEFAULTTONEAREST)).rcMonitor;
        SetWindowPos(_window, IntPtr.Zero, screen.Left, screen.Top, screen.Width, screen.Height,
            SWP_NOOWNERZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    /// <summary>Tells touch, pen and the mouse filter where the picture is on screen.</summary>
    private void UpdateRegion()
    {
        var window = _window;
        if (window == IntPtr.Zero || _closed || IsIconic(window) || !IsWindowVisible(window))
        {
            VideoRegion.Current = null;
            return;
        }

        if (!GetClientRect(window, out var client)) return;
        var origin = new POINT();
        if (!ClientToScreen(window, ref origin)) return;
        VideoRegion.Current = VideoRect.Letterbox(origin.X, origin.Y, client.Width, client.Height, VideoAspect);
    }

    public void Dispose()
    {
        lock (_soundGate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (!_closed && _window != IntPtr.Zero) PostMessage(_window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        StopMedia();
        _thread.Join(TimeSpan.FromSeconds(2));
        VideoRegion.Current = null;
    }

    private void StopMedia()
    {
        _video?.Dispose();
        SoundPassthrough? sound;
        lock (_soundGate)
        {
            // Set first, so a sound still starting is closed as soon as it has.
            _disposed = true;
            sound = _sound;
            _sound = null;
        }
        sound?.Dispose();
    }
}
