using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
    /// How the phone's screen is shown: false for a capture device in mpv, true for Windows'
    /// Wireless Display app. With the wireless display, touch is native and PhoneDeck sends the
    /// keyboard, mouse and pen. Applies the next time capture starts.
    /// </summary>
    public bool UseWirelessDisplay
    {
        get => _useWirelessDisplay;
        set { if (Set(ref _useWirelessDisplay, value)) Save(); }
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

    private int _viewerBrightness;
    private int _viewerContrast;
    private int _viewerSaturation;

    /// <summary>Built-in viewer picture levels, -50 to 50, 0 unchanged. Apply at once.</summary>
    public int ViewerBrightness
    {
        get => _viewerBrightness;
        set { if (Set(ref _viewerBrightness, ClampLevel(value))) Save(); }
    }

    public int ViewerContrast
    {
        get => _viewerContrast;
        set { if (Set(ref _viewerContrast, ClampLevel(value))) Save(); }
    }

    public int ViewerSaturation
    {
        get => _viewerSaturation;
        set { if (Set(ref _viewerSaturation, ClampLevel(value))) Save(); }
    }

    private static int ClampLevel(int value) =>
        Math.Clamp(value, PhoneDeck.Core.PictureLevels.Min, PhoneDeck.Core.PictureLevels.Max);

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

    /// <summary>The capture device's sound input, or empty for no sound (lowest delay).</summary>
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

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
