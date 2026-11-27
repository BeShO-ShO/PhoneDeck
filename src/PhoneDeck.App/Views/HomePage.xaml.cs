using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PhoneDeck.App.Services;
using PhoneDeck.Core;

namespace PhoneDeck.App.Views;

public partial class HomePage : Page
{
    private readonly PeripheralService _service = PeripheralService.Instance;

    public HomePage()
    {
        InitializeComponent();
        DataContext = _service;

        // Plugging the capture device in or out has no event here, so it is checked every few
        // seconds while Home is showing.
        _deviceCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _deviceCheck.Tick += async (_, _) => await ShowDeviceStatusAsync();

        // The receiver reports from its own threads; the page may be shown and left many times.
        Loaded += async (_, _) =>
        {
            CastService.Instance.StatusChanged += OnCastStatusChanged;
            AppSettings.Instance.PropertyChanged += OnSettingChanged;
            ShowCastStatus();
            _deviceCheck.Start();
            await ShowDeviceStatusAsync();
        };
        Unloaded += (_, _) =>
        {
            CastService.Instance.StatusChanged -= OnCastStatusChanged;
            AppSettings.Instance.PropertyChanged -= OnSettingChanged;
            _deviceCheck.Stop();
        };
    }

    private readonly DispatcherTimer _deviceCheck;
    private bool _checkingDevice;

    private async void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ViewerVideoDevice) or nameof(AppSettings.DisplayRoute))
            await ShowDeviceStatusAsync();
    }

    private async Task ShowDeviceStatusAsync()
    {
        if (_checkingDevice || !AppSettings.Instance.RouteIsCaptureDevice) return;
        _checkingDevice = true;
        try
        {
            var (video, _) = await ViewerService.ListDevicesAsync();
            var status = CaptureDeviceStatus.Describe(AppSettings.Instance.ViewerVideoDevice, video);
            DeviceStatusText.Text = status.Text;
            ShowProblem(DeviceStatusText, status.IsProblem);
        }
        finally
        {
            _checkingDevice = false;
        }
    }

    private void ShowProblem(System.Windows.Controls.TextBlock text, bool problem)
    {
        // TryFindResource: a missing theme key must not crash the page.
        if (TryFindResource(problem ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush")
            is System.Windows.Media.Brush brush)
            text.Foreground = brush;
    }

    private void OnCastStatusChanged() => Dispatcher.BeginInvoke(ShowCastStatus);

    private void ShowCastStatus()
    {
        var status = CastService.Instance.Status;
        CastStatusText.Text = status.Describe();
        ShowProblem(CastStatusText, status.IsProblem);
    }

    private void OnTargetChecked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TargetOption option) _service.Select(option);
    }

    private void OnDetails(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.ShowDetails();

    private async void OnToggleCapture(object sender, RoutedEventArgs e)
    {
        if (_service.IsCapturing) await _service.StopCaptureAsync();
        else await _service.StartCaptureAsync();
    }
}
