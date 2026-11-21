using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PhoneDeck.Core;
using Windows.Devices.Enumeration;

namespace PhoneDeck.App.Services;

/// <summary>
/// Starts mpv on the capture device with the low-delay options from the README, plus a control
/// pipe so touch can follow the picture exactly. PhoneDeck only ever closes an mpv it started.
/// </summary>
public sealed class ViewerService
{
    public static ViewerService Instance { get; } = new();

    private Process? _process;

    private ViewerService() { }

    /// <summary>The mpv PhoneDeck started, while it is running.</summary>
    public int? ProcessId
    {
        get
        {
            try { return _process is { HasExited: false } process ? process.Id : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public static bool IsConfigured => AppSettings.Instance.ViewerVideoDevice is not null;

    /// <summary>
    /// Opens mpv unless it is already open. Capture only does this while the Home switch is on;
    /// <paramref name="evenIfSwitchedOff"/> is for the Open now button. Returns a problem to show, or null.
    /// </summary>
    public string? EnsureRunning(Action<string> log, bool evenIfSwitchedOff = false)
    {
        var settings = AppSettings.Instance;
        if (!settings.OpenViewerWithCapture && !evenIfSwitchedOff) return null;
        if (settings.ViewerVideoDevice is not { } video) return null;
        if (ProcessId is not null) return null;

        // Another mpv (your own shortcut, say) already holds the capture device.
        if (Process.GetProcessesByName("mpv").Length > 0)
        {
            log("viewer: mpv is already running; not starting another");
            return null;
        }

        var mpv = FindMpv(settings.MpvPath);
        if (mpv is null)
        {
            log("viewer: mpv.exe not found");
            return "PhoneDeck could not find mpv.exe. Set its location in Settings.";
        }

        var start = new ProcessStartInfo(mpv) { UseShellExecute = false };
        foreach (var argument in Arguments(video, settings.ViewerAudioDevice)) start.ArgumentList.Add(argument);
        if (WriteInputConf(log) is { } conf) start.ArgumentList.Add($"--input-conf={conf}");

        try
        {
            _process = Process.Start(start);
            log($"viewer: started mpv ({Path.GetFileName(Path.GetDirectoryName(mpv))})");
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"viewer: mpv did not start: {ex.Message}");
            return $"mpv did not start: {ex.Message}";
        }
    }

    /// <summary>The mpv command line, also shown in Settings so it can be copied to a shortcut.</summary>
    public static List<string> Arguments(string video, string? audio)
    {
        // Windows' argument parsing would strip quotes anyway; ffmpeg's dshow input takes the
        // device names as they are, with spaces.
        var source = $"av://dshow:video={video}" + (audio is null ? "" : $":audio={audio}");
        var arguments = new List<string>
        {
            source,
            "--cache=no",
            "--osc=no",
            "--fs",
            "--cursor-autohide=always",
            // A pen stroke reaches mpv as a left-button drag, which by default moves a windowed
            // mpv along with the stroke. The title bar still moves it.
            "--no-window-dragging",
            // Less picture delay, which matters most for the mouse pointer: keep one presented
            // frame queued instead of two, always show the newest captured frame, and do not
            // wait for the display's refresh (fast motion may show a faint tear).
            "--swapchain-depth=1",
            "--video-latency-hacks=yes",
            "--d3d11-sync-interval=0",
            $@"--input-ipc-server=\\.\pipe\{MpvVideoLocator.PipeName}",
            "--title=PhoneDeck - phone screen"
        };
        // The default half-second capture audio buffer also delays the picture.
        if (audio is not null) arguments.Add("--demuxer-lavf-o=audio_buffer_size=50");
        return arguments;
    }

    /// <summary>
    /// Touches on the picture are for the phone, but Windows also hands them to mpv: a
    /// two-finger move arrives as mouse-wheel scrolling (mpv's volume) and a double tap as a
    /// double click (full screen). These bindings make mpv ignore the mouse; its keys still work.
    /// </summary>
    public const string InputConf = """
        # Written by PhoneDeck. Touches on the video belong to the phone.
        WHEEL_UP ignore
        WHEEL_DOWN ignore
        WHEEL_LEFT ignore
        WHEEL_RIGHT ignore
        MBTN_LEFT ignore
        MBTN_LEFT_DBL ignore
        MBTN_RIGHT ignore
        MBTN_MID ignore
        MBTN_BACK ignore
        MBTN_FORWARD ignore
        """;

    private static string? WriteInputConf(Action<string> log)
    {
        var path = Path.Combine(AppPaths.Root, "mpv-input.conf");
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(path, InputConf + "\n");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"viewer: could not write {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Closes the mpv PhoneDeck started, if it is still running.</summary>
    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                // A polite close first, so mpv releases the capture device cleanly.
                var closing = process.CloseMainWindow();
                if (!closing || !process.WaitForExit(1500)) process.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    /// <summary>The configured mpv, else one on PATH, else one in Program Files for this CPU.</summary>
    public static string? FindMpv(string? configured)
    {
        if (configured is not null) return File.Exists(configured) ? configured : null;

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), "mpv.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] found;
        try { found = Directory.GetFiles(programFiles, "mpv.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        // An x64 mpv runs on ARM64 under emulation, noticeably slower, so a native one wins.
        var native = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
        return found.Where(path => path.Contains("mpv", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => path.Contains(native, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>Names Windows gives the cameras and microphones; dshow uses the same ones.</summary>
    public static async Task<(List<string> Video, List<string> Audio)> ListDevicesAsync()
    {
        static async Task<List<string>> Names(DeviceClass deviceClass)
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(deviceClass);
                return devices.Where(device => device.IsEnabled).Select(device => device.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList();
            }
            catch (Exception) { return []; }
        }

        return (await Names(DeviceClass.VideoCapture), await Names(DeviceClass.AudioCapture));
    }
}
