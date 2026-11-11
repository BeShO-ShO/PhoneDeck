using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using BleHid.App.Services;
using BleHid.App.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BleHid.App;

public partial class MainWindow : FluentWindow
{
    private bool _warnedAboutTray;

    // Created once and kept, so each page keeps its state and its subscriptions are made once.
    private readonly HomePage _home = new();
    private readonly StatusPage _details = new();
    private readonly SettingsPage _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        // Applies the Mica backdrop and keeps tracking light/dark changes made while running.
        SystemThemeWatcher.Watch(this);
        Loaded += (_, _) => ShowHome();
    }

    public void ShowHome() => Show(_home, "Home");

    public void ShowDetails() => Show(_details, "Details");

    public void ShowSettings() => Show(_settings, "Settings");

    private void Show(Page page, string title)
    {
        PageHost.Content = page;
        PageTitle.Text = title;
        var home = page == _home;
        BackButton.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        DetailsButton.Visibility = page == _details ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = page == _settings ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowHome();

    private void OnDetails(object sender, RoutedEventArgs e) => ShowDetails();

    private void OnSettings(object sender, RoutedEventArgs e) => ShowSettings();

    // Staying resident keeps the GATT attribute table alive, which is what hosts reconnect to.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.Current.IsExiting)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        if (!AppSettings.Instance.CloseToTray)
        {
            App.Current.ExitApplication();
            return;
        }

        Hide();
        if (_warnedAboutTray) return;
        _warnedAboutTray = true;
        App.Current.Tray?.ShowMessage("Still running. Right-click the tray icon to exit.");
    }
}
