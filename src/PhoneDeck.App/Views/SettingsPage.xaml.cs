using System.Windows;
using System.Windows.Controls;
using PhoneDeck.App.Services;
using PhoneDeck.Core;

namespace PhoneDeck.App.Views;

public partial class SettingsPage : Page
{
    private SwitchGestureWatcher? _gestureWatcher;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = AppSettings.Instance;

        var available = StartupService.Command is not null;
        DevBar.IsOpen = !available;
        AutoStartToggle.IsEnabled = available;
        AutoStartToggle.IsChecked = available && StartupService.IsEnabled();

        SwitchGestureToggle.IsChecked =
            SwitchGestureOptions.Load(AppPaths.SwitchGestureConfig).Enabled;        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MpvPathBox.PlaceholderText = ViewerService.FindMpv(null) is { } found
            ? $"Found: {found}"
            : "Not found; enter the full path to mpv.exe";
        await FillDevicesAsync();

        if (_gestureWatcher is not null) return;
        _gestureWatcher = new SwitchGestureWatcher(AppPaths.SwitchGestureConfig);
        _gestureWatcher.Changed += OnGestureSettingsChanged;
        RefreshGestureToggle();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var watcher = _gestureWatcher;
        _gestureWatcher = null;
        if (watcher is null) return;
        watcher.Changed -= OnGestureSettingsChanged;
        watcher.Dispose();
    }

    private void OnGestureSettingsChanged(SwitchGestureOptions options) =>
        Dispatcher.BeginInvoke(RefreshGestureToggle);

    private void RefreshGestureToggle()
    {
        if (_gestureWatcher is { } watcher)
            SwitchGestureToggle.IsChecked = watcher.Current.Enabled;
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e) =>
        StartupService.Set(AutoStartToggle.IsChecked == true);

    private void OnSwitchGestureChanged(object sender, RoutedEventArgs e)
    {
        var enabled = SwitchGestureToggle.IsChecked == true;

        // Reload before writing so tuning values edited by hand are not flattened by a toggle,
        // and so a change made from the CLI meanwhile is not silently reverted.
        var updated = SwitchGestureOptions.Load(AppPaths.SwitchGestureConfig).WithEnabled(enabled);
        if (updated.TrySave(AppPaths.SwitchGestureConfig, out _)) return;

        // The setting did not persist, so the switch must not claim that it did.
        SwitchGestureToggle.IsChecked = !enabled;
    }

    private async Task FillDevicesAsync()
    {
        var (video, audio) = await ViewerService.ListDevicesAsync();
        // Filling the list must not overwrite what is typed or saved.
        var videoText = VideoDeviceBox.Text;
        var audioText = AudioDeviceBox.Text;
        VideoDeviceBox.ItemsSource = video;
        AudioDeviceBox.ItemsSource = audio;
        VideoDeviceBox.Text = videoText;
        AudioDeviceBox.Text = audioText;
    }

    private void OnOpenViewer(object sender, RoutedEventArgs e)
    {
        // Settings bound on lost focus; clicking the button may not have moved focus yet.
        VideoDeviceBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
        AudioDeviceBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
        MpvPathBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        if (!ViewerService.IsConfigured)
        {
            ViewerStatus.Text = "Choose the video device first.";
            return;
        }

        var problem = ViewerService.Instance.EnsureRunning(PeripheralService.Instance.LogApp, evenIfSwitchedOff: true);
        // mpv starts set up for capture; outside capture, give it the pointer and double tap back.
        if (problem is null) ViewerService.Instance.SetCaptureMode(PeripheralService.Instance.IsRedirecting);
        ViewerStatus.Text = problem ?? (ViewerService.Instance.ProcessId is null
            ? "mpv is already open."
            : "Opened. Press f in mpv if it is not full screen.");
    }

    private void OnExit(object sender, RoutedEventArgs e) => App.Current.ExitApplication();
}
