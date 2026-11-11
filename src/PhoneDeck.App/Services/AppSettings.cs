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
