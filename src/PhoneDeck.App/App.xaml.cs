using System.IO;
using System.Windows;
using System.Windows.Threading;
using PhoneDeck.App.Services;
using PhoneDeck.App.Views;
using PhoneDeck.Core;
using Wpf.Ui.Appearance;

namespace PhoneDeck.App;

public partial class App : Application
{
    private static readonly string LogPath = AppPaths.InLogs("PhoneDeck-app.log");

    private Mutex? _instance;
    private EventWaitHandle? _stopRequest;
    private RegisteredWaitHandle? _stopRegistration;
    private bool _exiting;
    private TouchOverlayWindow? _touchLayer;
    private CaptureHotkey? _captureHotkey;
    private ControlIndicator? _indicator;
    private bool _redirectUiActive;
    private bool _redirectWireless;
    private bool _redirectNoPicture;

    public new static App Current => (App)Application.Current;

    public TrayIconService? Tray { get; private set; }

    public bool IsExiting => _exiting;

    /// <summary>Settings turns the hotkey off while it records a new one.</summary>
    public CaptureHotkey? HotkeyRegistration => _captureHotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryTakeOwnership())
        {
            Shutdown();
            return;
        }

        // Lets `PhoneDeck --stop` shut this down the same way it shuts down background mode.
        _stopRequest = new EventWaitHandle(false, EventResetMode.ManualReset, SingleInstance.StopEventName);
        // A named event that still exists ignores the initial state above, so the stop request that
        // retired the previous owner would fire this one immediately.
        _stopRequest.Reset();
        _stopRegistration = ThreadPool.RegisterWaitForSingleObject(
            _stopRequest, (_, _) => Dispatcher.Invoke(ExitApplication), null, Timeout.Infinite,
            executeOnlyOnce: true);

        ApplicationThemeManager.ApplySystemTheme();
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Report(args.ExceptionObject as Exception);
        base.OnStartup(e);

        Tray = new TrayIconService(ShowMainWindow, ExitApplication);
        _captureHotkey = new CaptureHotkey(AppSettings.Instance.CaptureHotkeyValue, OnCaptureHotkey,
            PeripheralService.Instance.LogApp);
        _indicator = new ControlIndicator();
        AppSettings.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AppSettings.CaptureHotkey))
                _captureHotkey?.Change(AppSettings.Instance.CaptureHotkeyValue);
        };
        // A click or touch on PhoneDeck's own viewer hands input to the phone.
        ViewerService.Instance.PictureClicked += () => Dispatcher.BeginInvoke(OnPictureClicked);

        // The tray is the only surface visible while the window is hidden, which is exactly when
        // a lock left on a remote host would otherwise go unnoticed.
        var peripheral = PeripheralService.Instance;
        peripheral.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(peripheral.IsRedirecting))
            {
                Dispatcher.InvokeAsync(SyncTouchLayer);
                return;
            }

            if (args.PropertyName is not (nameof(peripheral.Target) or nameof(peripheral.TargetLockWarning))) return;
            Dispatcher.Invoke(() => Tray?.UpdateStatus(peripheral.Target, peripheral.TargetLockWarning));
        };

        // PhoneDeck's own cast receiver listens while the Wireless route is chosen. Changing the
        // route closes whichever viewer was open, so the next capture opens the right one.
        CastService.Instance.Apply(peripheral.LogApp);
        AppSettings.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(AppSettings.DisplayRoute) || _exiting) return;
            Dispatcher.InvokeAsync(() =>
            {
                ViewerService.Instance.Stop();
                CastService.Instance.Apply(peripheral.LogApp);
                TriggerRoutineWhenReceiving();
            });
        };

        var startHidden = e.Args.Contains(StartupService.TrayArgument, StringComparer.OrdinalIgnoreCase);
        MainWindow = new MainWindow();
        if (!startHidden) MainWindow.Show();

        // Auto-start matters most for the hidden launch, where there is no window to press Start in.
        if (startHidden) _ = StartResidentAsync();
        else if (AppSettings.Instance.StartPeripheralOnLaunch) _ = PeripheralService.Instance.StartAsync();
    }

    // A tray-only launch has no window, so the peripheral always starts. Capture is not armed:
    // the hotkey and clicking the picture start it, as after a normal launch. (It used to be armed
    // on This PC so the target-cycling keys could reach the phone; both are gone.)
    private static async Task StartResidentAsync() => await PeripheralService.Instance.StartAsync();

    /// <summary>
    /// The holder may be another copy of this app or the CLI's background service; either way it
    /// owns the radio and has to release it before this process can advertise.
    /// </summary>
    private bool TryTakeOwnership()
    {
        _instance = new Mutex(true, SingleInstance.MutexName, out var owned);
        if (owned) return true;

        var answer = MessageBox.Show(
            "PhoneDeck is already running, either in the notification area or as the command line "
            + "background service.\n\nStop it and continue?",
            "PhoneDeck", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;

        try
        {
            using var stop = EventWaitHandle.OpenExisting(SingleInstance.StopEventName);
            stop.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            MessageBox.Show(
                "The running instance is too old to accept a stop request. Exit it manually.",
                "PhoneDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        for (var attempt = 0; attempt < 50 && !owned; attempt++)
        {
            Thread.Sleep(100);
            _instance.Dispose();
            _instance = new Mutex(true, SingleInstance.MutexName, out owned);
        }

        if (!owned)
            MessageBox.Show("The running instance did not stop in time.",
                "PhoneDeck", MessageBoxButton.OK, MessageBoxImage.Warning);

        return owned;
    }

    /// <summary>
    /// Touch follows the keyboard and mouse: the layer is up exactly while input is going to the
    /// phone, however that started or ended, including the phone dropping out.
    /// </summary>
    private void SyncTouchLayer()
    {
        var peripheral = PeripheralService.Instance;

        if (peripheral.IsRedirecting && !_redirectUiActive && !_exiting)
        {
            _redirectUiActive = true;
            Tray?.SetLive(true);
            // The main window would otherwise sit on top of the video. It goes to the tray (or, with
            // that setting off, behind everything), and nothing brings it back afterwards.
            var mainWasActive = MainWindow is { IsVisible: true, IsActive: true };
            if (MainWindow is { IsVisible: true } main)
            {
                if (AppSettings.Instance.HideWindowOnCapture) main.Hide();
                else SendToBack(main);
            }

            // In wireless mode the bar is the only way to reach fill screen and send to back, and
            // the pen, so it shows whatever the touch switch says.
            _redirectWireless = AppSettings.Instance.DisplayRoute == DisplayRoute.WirelessDisplayApp;
            _redirectNoPicture = !DisplayRoutes.ShowsPicture(AppSettings.Instance.DisplayRoute);
            if (_redirectNoPicture)
            {
                // Keyboard and mouse only: DeX is on another screen, so there is no viewer and no
                // bar, and the keys (or the hotkey) bring control back.
                if (AppSettings.Instance.ShowControlIndicator) _indicator?.Show(ControlMessage(peripheral.Target));
                return;
            }
            if (_redirectWireless)
            {
                // Opened if needed but never closed by PhoneDeck: closing it ends the cast.
                WirelessDisplay.EnsureOpen(peripheral.LogApp);
            }
            else
            {
                // mpv stays open when capture stops, so the phone can still be watched; it closes
                // with PhoneDeck.
                if (ViewerService.Instance.EnsureRunning(peripheral.LogApp) is { } problem) Tray?.ShowMessage(problem);
                ViewerService.Instance.SetCaptureMode(true);
                // Started from the main window's button: the viewer takes over as the active
                // window, so its keys (F11, F, Esc) work once control comes back.
                if (mainWasActive) ViewerService.Instance.Activate();
            }

            if (AppSettings.Instance.ShowControlIndicator) _indicator?.Show(ControlMessage(peripheral.Target));
            if (!_redirectWireless && !AppSettings.Instance.TouchLayerEnabled) return;

            // The bar's X matches Ctrl+Alt+Q: capture stops but PhoneDeck keeps running, so the same
            // keys turn it straight back on.
            _touchLayer = new TouchOverlayWindow(peripheral.SendTouchFrameAsync, peripheral.SendPenAsync,
                peripheral.SendKeyTapsAsync, () => _ = peripheral.StopCaptureAsync(), peripheral.LogTouch,
                peripheral.Target, () => ViewerService.Instance.ProcessId, _redirectWireless,
                AppSettings.Instance.TouchOutsideReturns);
            _touchLayer.Show();
        }
        else if (!peripheral.IsRedirecting && _redirectUiActive)
        {
            _redirectUiActive = false;
            Tray?.SetLive(false);
            if (AppSettings.Instance.ShowControlIndicator && !_exiting) _indicator?.Show("Back on this PC");
            if (!_exiting && !_redirectWireless && !_redirectNoPicture) ViewerService.Instance.SetCaptureMode(false);
            _touchLayer?.Close();
            _touchLayer = null;
            // Window order is left exactly as it is: the main window stays behind.
        }
    }

    private static void SendToBack(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE, NOMOVE, NOACTIVATE */);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>The note shown when the phone takes control: whose it is and the quickest way back.</summary>
    private static string ControlMessage(string target)
    {
        var bracket = target.IndexOf(" [", StringComparison.Ordinal);
        var phone = bracket > 0 ? target[..bracket] : target;
        var settings = AppSettings.Instance;
        var back = settings.CtrlAltTapReturns ? "tap Ctrl+Alt" : settings.CaptureHotkeyText;
        return $"{phone} has the keyboard and mouse  ·  {back} to return";
    }

    private void OnPictureClicked()
    {
        if (!AppSettings.Instance.ClickPictureToControl) return;
        OnCaptureHotkey();
    }

    /// <summary>The capture hotkey while capture is off: the keyboard hook handles it while capture is on.</summary>
    private void OnCaptureHotkey()
    {
        var peripheral = PeripheralService.Instance;
        if (_exiting || peripheral.IsCapturing) return;
        // Belt and braces with no-repeat: the press that just ended capture must not restart it.
        if (Environment.TickCount64 - peripheral.CaptureEndedAt < 750) return;

        if (!peripheral.CanCapture)
        {
            Tray?.ShowMessage("No phone connected. Tap Connect for this PC in the phone's Bluetooth settings.");
            return;
        }

        _ = peripheral.StartCaptureAsync();
    }

    public void ShowMainWindow()
    {
        MainWindow ??= new MainWindow();
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    /// <summary>
    /// After choosing Wireless, starts the phone's routine once PhoneDeck's receiver is listening,
    /// or straight away for the Wireless Display app. Earlier, the routine's cast could reach
    /// Windows' own receiver while PhoneDeck's was still starting.
    /// </summary>
    private void TriggerRoutineWhenReceiving()
    {
        var route = AppSettings.Instance.DisplayRoute;
        if (!AppSettings.Instance.TriggerPhoneRoutine || !DisplayRoutes.IsWireless(route)) return;
        if (route != DisplayRoute.Cast)
        {
            _ = PeripheralService.Instance.RestartForRoutineAsync();
            return;
        }

        var cast = CastService.Instance;
        void Check()
        {
            if (_exiting || AppSettings.Instance.DisplayRoute != DisplayRoute.Cast)
            {
                cast.StatusChanged -= OnStatus;
                return;
            }
            switch (cast.Status.State)
            {
                case CastState.Waiting:
                    cast.StatusChanged -= OnStatus;
                    _ = PeripheralService.Instance.RestartForRoutineAsync();
                    break;
                case CastState.Connected or CastState.Failed or CastState.Unsupported:
                    cast.StatusChanged -= OnStatus;
                    break;
            }
        }
        void OnStatus() => Dispatcher.BeginInvoke(Check);
        cast.StatusChanged += OnStatus;
        Check();
    }

    public async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        PeripheralService.Instance.LogApp("exiting");
        // Ending a cast can take up to about 30 s (CastRetryWindow), so the window goes at once.
        MainWindow?.Hide();
        // A failed stop must not strand the app: _exiting is already set, so Exit would never retry.
        try { await PeripheralService.Instance.StopAsync(); }
        catch (Exception ex) { Report(ex); }
        Tray?.Dispose();
        _captureHotkey?.Dispose();
        _indicator?.Close();
        // The window lets go of the cast's picture before the cast's player is closed.
        ViewerService.Instance.Stop();
        try { await CastService.Instance.StopForExitAsync(); }
        catch (Exception ex) { Report(ex); }
        // A filled window would otherwise be remembered with its title bar off screen.
        try { WirelessDisplay.RestoreIfFilled(); }
        catch (Exception ex) { Report(ex); }
        // Shutdown closes windows, so it must not run inside a close that is still unwinding.
        await Dispatcher.InvokeAsync(Shutdown);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _stopRegistration?.Unregister(null);
        _stopRequest?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    // A XAML error on one page used to terminate the process with no output at all.
    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception);
        e.Handled = true;
        MessageBox.Show($"{e.Exception.Message}\n\nLogged to {LogPath}",
            "PhoneDeck", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void Report(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            // The tray build can run for weeks, so the log cannot grow without a bound.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 5 * 1024 * 1024) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:s}  {ex}\n\n");
        }
        // Runs inside the exception handlers, so a log it cannot write must not raise another.
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
    }
}
