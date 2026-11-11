using System.Collections.Specialized;
using System.Windows.Controls;
using BleHid.App.Services;

namespace BleHid.App.Views;

public partial class StatusPage : Page
{
    private readonly PeripheralService _service = PeripheralService.Instance;

    public StatusPage()
    {
        InitializeComponent();
        DataContext = _service;
        _service.Log.CollectionChanged += OnLogChanged;
        Unloaded += (_, _) => _service.Log.CollectionChanged -= OnLogChanged;
        // On Loaded rather than here: pages are kept, and the setting can change in between.
        Loaded += (_, _) => ChordHint.Visibility = AppSettings.Instance.EnableCtrlDCSwitch
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) LogScroller.ScrollToEnd();
    }

    // Dragging past the end of the log otherwise makes Windows bounce the whole window.
    private void OnBoundaryFeedback(object? sender, System.Windows.Input.ManipulationBoundaryFeedbackEventArgs e) =>
        e.Handled = true;

    private async void OnStart(object sender, System.Windows.RoutedEventArgs e) =>
        await _service.StartAsync();

    private async void OnStop(object sender, System.Windows.RoutedEventArgs e) =>
        await _service.StopAsync();
}
