using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            SwitchGestureOptions.Load(AppPaths.SwitchGestureConfig).Enabled;
        ShowHotkey();
        Loaded += OnLoaded;
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

    private void ShowHotkey() => HotkeyBox.Text = AppSettings.Instance.CaptureHotkeyText;

    // While the box records, the current hotkey must not turn capture on.
    private void OnHotkeyBoxFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        App.Current.HotkeyRegistration?.Suspend();
        HotkeyBox.Text = "Press the new keys…";
        HotkeyStatus.Text = "Use Ctrl, Alt or Win with a letter, a digit or F1 to F12. Esc cancels.";
    }

    private void OnHotkeyBoxUnfocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        App.Current.HotkeyRegistration?.Resume();
        ShowHotkey();
    }

    private void OnHotkeyBoxKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            HotkeyStatus.Text = "";
            EndRecording();
            return;
        }

        var held = Keyboard.Modifiers;
        var modifiers = HotkeyModifiers.None;
        if (held.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Ctrl;
        if (held.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (held.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (held.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Win;

        // Only modifiers so far: show them and wait for the key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
            or Key.RightShift or Key.LWin or Key.RWin)
        {
            // "Ctrl+Alt+0x00" without the made-up key.
            var partial = new Hotkey(modifiers, 0).ToString();
            var cut = partial.LastIndexOf('+');
            HotkeyBox.Text = cut > 0 ? partial[..cut] + "+…" : "Press the new keys…";
            return;
        }

        var hotkey = new Hotkey(modifiers, KeyInterop.VirtualKeyFromKey(key));
        if (!hotkey.IsValid)
        {
            HotkeyStatus.Text = "That can't be the hotkey. Use Ctrl, Alt or Win with a letter, a digit or F1 to F12.";
            return;
        }

        AppSettings.Instance.CaptureHotkey = hotkey.ToString();
        HotkeyStatus.Text = $"Saved. {hotkey} now turns control of the phone on and off.";
        EndRecording();
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.CaptureHotkey = null;
        HotkeyStatus.Text = "Back to Ctrl+Alt+Q.";
        ShowHotkey();
    }

    private void EndRecording()
    {
        // Leaving the box re-registers the hotkey and shows it.
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(HotkeyBox), null);
        ShowHotkey();
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

        var viewer = ViewerService.Instance;
        var wasOpen = viewer.IsBuiltInOpen;
        var problem = viewer.EnsureRunning(PeripheralService.Instance.LogApp, evenIfSwitchedOff: true);
        // The viewer starts set up for capture; outside capture, give it the pointer and double tap back.
        if (problem is null) viewer.SetCaptureMode(PeripheralService.Instance.IsRedirecting);
        ViewerStatus.Text = problem ?? (AppSettings.Instance.UseBuiltInViewer
            ? wasOpen
                ? "The viewer is already open."
                : "Opened. Double-click it or press F11 to switch between full screen and a window."
            : viewer.ProcessId is null
                ? "mpv is already open."
                : "Opened. Press f in mpv if it is not full screen.");
    }

    private void OnExit(object sender, RoutedEventArgs e) => App.Current.ExitApplication();
}
