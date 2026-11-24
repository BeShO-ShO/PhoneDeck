using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PhoneDeck.App.Viewer;
using PhoneDeck.Core;
using Windows.Devices.Enumeration;

namespace PhoneDeck.App.Services;

/// <summary>
/// Shows the capture device, in PhoneDeck's own window (<see cref="CaptureViewer"/>) or in mpv,
/// as Settings says. mpv gets the low-delay options from the README, plus a control pipe so
/// touch can follow the picture exactly. PhoneDeck only ever closes an mpv it started.
/// </summary>
public sealed class ViewerService
{
    public static ViewerService Instance { get; } = new();

    private Process? _process;
    private CaptureViewer? _builtIn;

    private ViewerService()
    {
        // Picture changes in Settings show in an open viewer straight away.
        AppSettings.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AppSettings.ViewerRange) or nameof(AppSettings.ViewerBrightness)
                or nameof(AppSettings.ViewerContrast) or nameof(AppSettings.ViewerSaturation))
                ApplyPicture();
        };
    }

    private void ApplyPicture()
    {
        var settings = AppSettings.Instance;
        _builtIn?.SetPicture((PictureRange)settings.ViewerRange,
            new PictureLevels(settings.ViewerBrightness, settings.ViewerContrast, settings.ViewerSaturation));
    }

    /// <summary>PhoneDeck's own viewer window is open.</summary>
    public bool IsBuiltInOpen => _builtIn is { IsOpen: true };

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
        string? audio = null;
        if (settings.ViewerSound)
        {
            audio = settings.ViewerAudioDevice ?? FindSoundInput(video);
            if (settings.ViewerAudioDevice is null)
                log(audio is null ? $"viewer: no sound input found on \"{video}\"; choose one in Settings"
                    : $"viewer: sound from the same device: \"{audio}\"");
        }

        if (settings.UseBuiltInViewer)
        {
            if (IsBuiltInOpen) return null;
            _builtIn?.Dispose();
            log($"viewer: opening the built-in viewer{(audio is null ? " without sound" : "")}");
            _builtIn = new CaptureViewer(video, audio, log);
            ApplyPicture();
            return _builtIn.IsOpen ? null : "PhoneDeck could not open its viewer window. See the log.";
        }

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
        foreach (var argument in Arguments(video, audio)) start.ArgumentList.Add(argument);
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

    /// <summary>mpv's cursor-autohide while input goes to the phone, and while it does not.</summary>
    public const string PointerHiddenWhileCapturing = "always";
    public const string PointerHiddenWhenIdle = "1000";

    /// <summary>What a double tap or double click on the picture does in each mode.</summary>
    public const string DoubleTapWhileCapturing = "ignore";
    public const string DoubleTapWhenIdle = "cycle fullscreen";

    private int _modeRequest;

    /// <summary>
    /// Fits mpv to whether input is going to the phone. Works with any mpv on PhoneDeck's control
    /// pipe, including one from your own shortcut.
    /// <list type="bullet">
    /// <item>Pointer: while capturing, PhoneDeck keeps parking the Windows pointer in the middle of
    /// the screen, over the picture, so mpv hides it there. Otherwise mpv hides it only once it
    /// stops moving, or it vanishes over mpv when you want to use it.</item>
    /// <item>Double tap: while capturing it belongs to the phone, so mpv ignores it. Otherwise it
    /// switches full screen, mpv's usual behaviour, so touch alone can get in and out of it.</item>
    /// </list>
    /// </summary>
    public void SetCaptureMode(bool capturing)
    {
        _builtIn?.SetCapturing(capturing);
        // The built-in viewer has no pipe; an mpv from your own shortcut still might.
        if (IsBuiltInOpen) return;

        var request = Interlocked.Increment(ref _modeRequest);
        var pointer = capturing ? PointerHiddenWhileCapturing : PointerHiddenWhenIdle;
        var doubleTap = capturing ? DoubleTapWhileCapturing : DoubleTapWhenIdle;
        _ = Task.Run(async () =>
        {
            // An mpv that has just started needs a moment before its pipe answers. A newer
            // request (capture toggled again) takes over from this one.
            for (var attempt = 0; attempt < 10 && request == Volatile.Read(ref _modeRequest); attempt++)
            {
                if (await MpvVideoLocator.SendCommandAsync("set", "cursor-autohide", pointer))
                {
                    // keybind replaces the input file's binding for the double click.
                    await MpvVideoLocator.SendCommandAsync("keybind", "MBTN_LEFT_DBL", doubleTap);
                    return;
                }
                await Task.Delay(300);
            }
        });
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
            // Started for capture, so hidden; SetCaptureMode relaxes it when capture stops.
            $"--cursor-autohide={PointerHiddenWhileCapturing}",
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

    /// <summary>
    /// Switches the viewer between full screen and a window: the built-in one directly, mpv
    /// through its pipe. Returns false if neither answered.
    /// </summary>
    public async Task<bool> ToggleFullScreenAsync()
    {
        if (_builtIn is { IsOpen: true } viewer)
        {
            viewer.ToggleFullScreen();
            return true;
        }
        return await MpvVideoLocator.SendCommandAsync("cycle", "fullscreen");
    }

    /// <summary>Closes the viewer PhoneDeck opened (its own window, or the mpv it started).</summary>
    public void Stop()
    {
        var builtIn = _builtIn;
        _builtIn = null;
        builtIn?.Dispose();

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

    private const string ContainerIdKey = "System.Devices.ContainerId";

    /// <summary>
    /// The sound input built into the same capture device as <paramref name="video"/>, or null.
    /// Waits briefly on the thread pool: Windows answers in well under a second.
    /// </summary>
    public static string? FindSoundInput(string video)
    {
        static Guid? Container(DeviceInformation device) =>
            device.Properties.TryGetValue(ContainerIdKey, out var value) && value is Guid id ? id : null;

        try
        {
            var search = Task.Run(async () =>
            {
                var keys = new[] { ContainerIdKey };
                var cameras = await DeviceInformation.FindAllAsync(Windows.Media.Devices.MediaDevice.GetVideoCaptureSelector(), keys);
                var camera = cameras.FirstOrDefault(device => string.Equals(device.Name, video, StringComparison.OrdinalIgnoreCase));
                if (camera is null) return null;
                var inputs = await DeviceInformation.FindAllAsync(Windows.Media.Devices.MediaDevice.GetAudioCaptureSelector(), keys);
                return CaptureSound.SameDevice(Container(camera),
                    inputs.Where(device => device.IsEnabled).Select(device => (device.Name, Container(device))));
            });
            return search.Wait(TimeSpan.FromSeconds(3)) ? search.Result : null;
        }
        catch (Exception) { return null; }
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
