using System.IO;
using System.Windows;
using System.Windows.Threading;
using BleHid.App.Services;
using BleHid.App.Views;
using BleHid.Core;
using Wpf.Ui.Appearance;

namespace BleHid.App;

public partial class App : Application
{
    private static readonly string LogPath = AppPaths.InLogs("blehid-app.log");

    private Mutex? _instance;
    private EventWaitHandle? _stopRequest;
    private RegisteredWaitHandle? _stopRegistration;
    private bool _exiting;
    private TouchOverlayWindow? _touchLayer;
    private CaptureHotkey? _captureHotkey;
    private bool _redirectUiActive;
    private bool _restoreWindowAfterRedirect;

    public new static App Current => (App)Application.Current;

    public TrayIconService? Tray { get; private set; }

    public bool IsExiting => _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryTakeOwnership())
        {
            Shutdown();
            return;
        }

        // Lets `blehid --stop` shut this down the same way it shuts down background mode.
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
        _captureHotkey = new CaptureHotkey(OnCaptureHotkey, PeripheralService.Instance.LogApp);

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

        var startHidden = e.Args.Contains(StartupService.TrayArgument, StringComparer.OrdinalIgnoreCase);
        MainWindow = new MainWindow();
        if (!startHidden) MainWindow.Show();

        // Auto-start matters most for the hidden launch, where there is no window to press Start in.
        if (startHidden) _ = StartResidentAsync();
        else if (AppSettings.Instance.StartPeripheralOnLaunch) _ = PeripheralService.Instance.StartAsync();
    }

    // A tray-only launch has no UI to arm capture from, so the hotkeys have to work on their own.
    private static async Task StartResidentAsync()
    {
        await PeripheralService.Instance.StartAsync();
        await PeripheralService.Instance.StartCaptureAsync(armEarly: true);
    }

    /// <summary>
    /// The holder may be another copy of this app or the CLI's background service; either way it
    /// owns the radio and has to release it before this process can advertise.
    /// </summary>
    private bool TryTakeOwnership()
    {
        _instance = new Mutex(true, SingleInstance.MutexName, out var owned);
        if (owned) return true;

        var answer = MessageBox.Show(
            "BLE HID is already running, either in the notification area or as the command line "
            + "background service.\n\nStop it and continue?",
            "BLE HID", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
                "BLE HID", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                "BLE HID", MessageBoxButton.OK, MessageBoxImage.Warning);

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
            // The main window would otherwise sit on top of the video, with or without the layer.
            _restoreWindowAfterRedirect = MainWindow?.IsVisible == true;
            MainWindow?.Hide();

            if (!AppSettings.Instance.TouchLayerEnabled) return;

            // The bar's X matches Ctrl+Alt+Q: capture stops but BleHid keeps running, so the same
            // keys turn it straight back on.
            _touchLayer = new TouchOverlayWindow(peripheral.SendTouchAsync, peripheral.SendKeyTapsAsync,
                () => _ = peripheral.StopCaptureAsync(), peripheral.LogTouch, peripheral.Target);
            _touchLayer.Show();
        }
        else if (!peripheral.IsRedirecting && _redirectUiActive)
        {
            _redirectUiActive = false;
            _touchLayer?.Close();
            _touchLayer = null;
            if (_restoreWindowAfterRedirect && !_exiting) ShowMainWindow();
        }
    }

    /// <summary>Ctrl+Alt+Q while capture is off: the keyboard hook handles it while capture is on.</summary>
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

    public async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        // A failed stop must not strand the app: _exiting is already set, so Exit would never retry.
        try { await PeripheralService.Instance.StopAsync(); }
        catch (Exception ex) { Report(ex); }
        Tray?.Dispose();
        _captureHotkey?.Dispose();
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
            "BLE HID", MessageBoxButton.OK, MessageBoxImage.Error);
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
