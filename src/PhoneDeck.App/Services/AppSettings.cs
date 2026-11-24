using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhoneDeck.Core;

namespace PhoneDeck.App.Services;

/// <summary>
/// User preferences, persisted next to the background log so the CLI and the UI agree on a home.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private static readonly string Path = System.IO.Path.Combine(
        PhoneDeck.Core.AppPaths.Root, "app-settings.json");

    public static AppSettings Instance { get; } = Load();

    private bool _closeToTray = true;
    public bool CloseToTray
    {
        get => _closeToTray;
        set { if (Set(ref _closeToTray, value)) Save(); }
    }

    private bool _startPeripheralOnLaunch = true;
    public bool StartPeripheralOnLaunch
    {
        get => _startPeripheralOnLaunch;
        set { if (Set(ref _startPeripheralOnLaunch, value)) Save(); }
    }

    private bool _enableCapsLockSwitch;

    /// <summary>
    /// Off by default: with one phone, Ctrl+Alt+Q already switches between it and this PC, and a
    /// stray Ctrl+Alt+CapsLock would leave capture running with input on this PC.
    /// </summary>
    public bool EnableCapsLockSwitch
    {
        get => _enableCapsLockSwitch;
        set { if (Set(ref _enableCapsLockSwitch, value)) Save(); }
    }

    private bool _enableCtrlDCSwitch;

    /// <summary>Off by default: the chord can leak a keypress to the host being controlled.</summary>
    public bool EnableCtrlDCSwitch
    {
        get => _enableCtrlDCSwitch;
        set { if (Set(ref _enableCtrlDCSwitch, value)) Save(); }
    }

    private bool _touchLayerEnabled = true;

    /// <summary>Whether capture also opens the see-through touch layer over the video.</summary>
    public bool TouchLayerEnabled
    {
        get => _touchLayerEnabled;
        set { if (Set(ref _touchLayerEnabled, value)) Save(); }
    }

    private bool _evenPointerPacing = true;

    /// <summary>Spreads pointer motion evenly over reports (see PointerResampler); applies live.</summary>
    public bool EvenPointerPacing
    {
        get => _evenPointerPacing;
        set { if (Set(ref _evenPointerPacing, value)) Save(); }
    }

    private bool _useWirelessDisplay;

    /// <summary>
    /// The setting from before PhoneDeck had its own receiver: true meant Windows' Wireless
    /// Display app. Only read to choose <see cref="DisplayRoute"/> for settings saved back then.
    /// </summary>
    public bool UseWirelessDisplay
    {
        get => _useWirelessDisplay;
        set { if (Set(ref _useWirelessDisplay, value)) Save(); }
    }

    private int? _savedDisplayRoute;

    /// <summary>The saved <see cref="DisplayRoute"/>; null in settings saved before it existed.</summary>
    public int? SavedDisplayRoute
    {
        get => _savedDisplayRoute;
        set { if (Set(ref _savedDisplayRoute, value)) Save(); }
    }

    /// <summary>
    /// How the phone's screen is shown: a capture device, PhoneDeck's own cast receiver, or
    /// Windows' Wireless Display app. Applies the next time capture starts; the receiver starts
    /// or stops at once.
    /// </summary>
    [JsonIgnore]
    public DisplayRoute DisplayRoute
    {
        get => DisplayRoutes.FromSaved(_savedDisplayRoute, _useWirelessDisplay);
        set
        {
            if (value == DisplayRoute && _savedDisplayRoute is not null) return;
            SavedDisplayRoute = (int)value;
            OnPropertyChanged(nameof(DisplayRoute));
            OnPropertyChanged(nameof(RouteIsCaptureDevice));
            OnPropertyChanged(nameof(RouteIsWireless));
            OnPropertyChanged(nameof(RouteIsCast));
            OnPropertyChanged(nameof(RouteIsWirelessDisplayApp));
        }
    }

    private bool? _savedWirelessUsesApp;

    /// <summary>The receiver chosen in Settings; null in settings saved before it existed.</summary>
    public bool? SavedWirelessUsesApp
    {
        get => _savedWirelessUsesApp;
        set { if (Set(ref _savedWirelessUsesApp, value)) Save(); }
    }

    /// <summary>
    /// Which receiver Wireless on Home uses: PhoneDeck's own, or Windows' Wireless Display app as
    /// the backup. Kept while the capture device is chosen, so switching back to Wireless uses it.
    /// </summary>
    [JsonIgnore]
    public bool WirelessUsesApp
    {
        get => DisplayRoutes.UsesApp(_savedWirelessUsesApp, DisplayRoute);
        set
        {
            SavedWirelessUsesApp = value;
            OnPropertyChanged(nameof(WirelessUsesApp));
            OnPropertyChanged(nameof(WirelessIsPhoneDeck));
            OnPropertyChanged(nameof(WirelessIsApp));
            if (DisplayRoutes.IsWireless(DisplayRoute)) DisplayRoute = DisplayRoutes.Wireless(value);
        }
    }

    // One per radio button, so XAML can bind each without a converter.
    [JsonIgnore]
    public bool WirelessIsPhoneDeck
    {
        get => !WirelessUsesApp;
        set { if (value) WirelessUsesApp = false; }
    }

    [JsonIgnore]
    public bool WirelessIsApp
    {
        get => WirelessUsesApp;
        set { if (value) WirelessUsesApp = true; }
    }

    [JsonIgnore]
    public bool RouteIsCaptureDevice
    {
        get => DisplayRoute == DisplayRoute.CaptureDevice;
        set { if (value) DisplayRoute = DisplayRoute.CaptureDevice; }
    }

    [JsonIgnore]
    public bool RouteIsWireless
    {
        get => DisplayRoutes.IsWireless(DisplayRoute);
        set { if (value) DisplayRoute = DisplayRoutes.Wireless(WirelessUsesApp); }
    }

    [JsonIgnore]
    public bool RouteIsCast
    {
        get => DisplayRoute == DisplayRoute.Cast;
        set { if (value) DisplayRoute = DisplayRoute.Cast; }
    }

    [JsonIgnore]
    public bool RouteIsWirelessDisplayApp
    {
        get => DisplayRoute == DisplayRoute.WirelessDisplayApp;
        set { if (value) DisplayRoute = DisplayRoute.WirelessDisplayApp; }
    }

    private bool _openViewerWithCapture = true;

    /// <summary>Whether starting capture also opens mpv on <see cref="ViewerVideoDevice"/>.</summary>
    public bool OpenViewerWithCapture
    {
        get => _openViewerWithCapture;
        set { if (Set(ref _openViewerWithCapture, value)) Save(); }
    }

    private bool _useBuiltInViewer = true;

    /// <summary>
    /// Shows the capture device in PhoneDeck's own window (true) or in mpv (false). Both stay
    /// available. Applies the next time the viewer opens.
    /// </summary>
    public bool UseBuiltInViewer
    {
        get => _useBuiltInViewer;
        set { if (Set(ref _useBuiltInViewer, value)) Save(); }
    }

    private bool _viewerSound = true;

    /// <summary>
    /// Plays the capture device's sound (from <see cref="ViewerAudioDevice"/>). Off can mean
    /// less delay, in mpv above all. Applies the next time the viewer opens.
    /// </summary>
    public bool ViewerSound
    {
        get => _viewerSound;
        set { if (Set(ref _viewerSound, value)) Save(); }
    }

    private int _viewerRange;

    /// <summary>
    /// The built-in viewer's black and white levels: 0 as detected, 1 full (0 to 255), 2 limited
    /// (16 to 235). As an index, so a combo box can bind to it. Applies at once.
    /// </summary>
    public int ViewerRange
    {
        get => _viewerRange;
        set { if (Set(ref _viewerRange, Math.Clamp(value, 0, 2))) Save(); }
    }

    private string? _viewerVideoDevice;

    /// <summary>
    /// The capture device mpv shows. When set, starting capture also opens mpv on it if it is
    /// not already open. Empty means PhoneDeck leaves mpv to you.
    /// </summary>
    public string? ViewerVideoDevice
    {
        get => _viewerVideoDevice;
        set { if (Set(ref _viewerVideoDevice, string.IsNullOrWhiteSpace(value) ? null : value.Trim())) Save(); }
    }

    private string? _viewerAudioDevice;

    /// <summary>
    /// The sound input to play. Empty means the capture device's own (found by
    /// <see cref="ViewerService.FindSoundInput"/>); <see cref="ViewerSound"/> turns sound off.
    /// </summary>
    public string? ViewerAudioDevice
    {
        get => _viewerAudioDevice;
        set { if (Set(ref _viewerAudioDevice, string.IsNullOrWhiteSpace(value) ? null : value.Trim())) Save(); }
    }

    private string? _mpvPath;

    /// <summary>Full path to mpv.exe; empty means find it on PATH or in Program Files.</summary>
    public string? MpvPath
    {
        get => _mpvPath;
        set { if (Set(ref _mpvPath, string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"'))) Save(); }
    }

    private string? _lastHostId;

    /// <summary>The host last chosen as the target, reselected when it reconnects after a restart.</summary>
    public string? LastHostId
    {
        get => _lastHostId;
        set { if (Set(ref _lastHostId, value)) Save(); }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file is not worth blocking startup over.
        }

        return new AppSettings();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
