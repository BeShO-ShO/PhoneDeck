using System.Collections.Specialized;
using System.Windows.Controls;
using PhoneDeck.App.Services;

namespace PhoneDeck.App.Views;

public partial class StatusPage : Page
{
    private readonly PeripheralService _service = PeripheralService.Instance;

    public StatusPage()
    {
        InitializeComponent();
        DataContext = _service;
        _service.Log.CollectionChanged += OnLogChanged;
        Unloaded += (_, _) => _service.Log.CollectionChanged -= OnLogChanged;
        // On Loaded rather than here: pages are kept, and the settings can change in between.
        Loaded += (_, _) => ShowSwitchKeys();
    }

    /// <summary>Lists only the target-switch keys that are turned on, and hides the card if none are.</summary>
    private void ShowSwitchKeys()
    {
        var caps = AppSettings.Instance.EnableCapsLockSwitch;
        var chord = AppSettings.Instance.EnableCtrlDCSwitch;
        static System.Windows.Visibility Show(bool on) =>
            on ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        SwitchCard.Visibility = Show(caps || chord);
        CapsHint.Visibility = Show(caps);
        ChordHint.Visibility = Show(chord);
        ChordHint.Text = caps ? "or Ctrl + D + C" : "Ctrl + D + C";
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
