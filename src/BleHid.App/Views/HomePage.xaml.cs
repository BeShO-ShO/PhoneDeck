using System.Windows;
using System.Windows.Controls;
using BleHid.App.Services;

namespace BleHid.App.Views;

public partial class HomePage : Page
{
    private readonly PeripheralService _service = PeripheralService.Instance;

    public HomePage()
    {
        InitializeComponent();
        DataContext = _service;
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
