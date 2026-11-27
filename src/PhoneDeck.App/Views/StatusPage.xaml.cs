using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PhoneDeck.App.Services;
using PhoneDeck.Core;

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
        Loaded += (_, _) =>
        {
            ShowShortcuts();
            LogActionStatus.Visibility = Visibility.Collapsed;
        };
    }

    private void ShowShortcuts()
    {
        var settings = AppSettings.Instance;
        ShortcutItems.ItemsSource = ShortcutList.Build(settings.CaptureHotkeyText,
            settings.ClickPictureToControl, settings.CtrlAltTapReturns, settings.CopilotKeyReturns,
            settings.TouchOutsideReturns, settings.UseBuiltInViewer,
            DisplayRoutes.ShowsPicture(settings.DisplayRoute));
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, _service.Log));
            ShowLogAction("Copied. It includes your phone's name and Bluetooth address, so check it before sharing.");
        }
        // The clipboard can be held by another app for a moment.
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ShowLogAction("The clipboard is busy; try again.");
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        var path = _service.LogFilePath;
        if (!File.Exists(path))
        {
            ShowLogAction("There is no log file yet.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowLogAction($"Couldn't open it: {ex.Message}. It is at {path}");
        }
    }

    private void ShowLogAction(string text)
    {
        LogActionStatus.Text = text;
        LogActionStatus.Visibility = Visibility.Visible;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) LogScroller.ScrollToEnd();
    }

    // Dragging past the end of the log otherwise makes Windows bounce the whole window.
    private void OnBoundaryFeedback(object? sender, System.Windows.Input.ManipulationBoundaryFeedbackEventArgs e) =>
        e.Handled = true;

    private async void OnStart(object sender, RoutedEventArgs e) =>
        await _service.StartAsync();

    private async void OnStop(object sender, RoutedEventArgs e) =>
        await _service.StopAsync();
}
