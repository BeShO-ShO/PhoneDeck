using System.Windows;
using System.Windows.Controls;
using PhoneDeck.App.Services;

namespace PhoneDeck.App.Views;

public partial class HomePage : Page
{
    private readonly PeripheralService _service = PeripheralService.Instance;

    public HomePage()
    {
        InitializeComponent();
        DataContext = _service;

        // The receiver reports from its own threads; the page may be shown and left many times.
        Loaded += (_, _) =>
        {
            CastService.Instance.StatusChanged += OnCastStatusChanged;
            ShowCastStatus();
        };
        Unloaded += (_, _) => CastService.Instance.StatusChanged -= OnCastStatusChanged;
    }

    private void OnCastStatusChanged() => Dispatcher.BeginInvoke(ShowCastStatus);

    private void ShowCastStatus()
    {
        var status = CastService.Instance.Status;
        CastStatusText.Text = status.Describe();
        // TryFindResource: a missing theme key must not crash the page.
        if (TryFindResource(status.IsProblem ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush")
            is System.Windows.Media.Brush brush)
            CastStatusText.Foreground = brush;
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
