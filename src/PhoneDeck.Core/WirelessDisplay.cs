using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneDeck.Core;

/// <summary>A window rectangle in screen pixels; Right and Bottom are exclusive.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Windows' Wireless Display app (the Miracast receiver), used as the phone's screen instead of a
/// capture device. Its window is an ApplicationFrameWindow holding Receiver.exe's CoreWindow,
/// with a title bar across the top and the picture letterboxed in the area below it.
///
/// Everything here only moves or reorders that window, the same as dragging it would. Nothing
/// about the app or its window is changed. That matters because the cast ends the moment the
/// window is minimised, and also when it is in its own full screen (Win+Shift+Enter) and another
/// app is switched to. Covering it is safe, so "fill screen" stretches the ordinary window over
/// the monitor instead, and "send to back" stands in for minimising.
/// </summary>
public static class WirelessDisplay
{
    public const string AppId = "Microsoft.PPIProjection_cw5n1h2txyewy!Microsoft.PPIProjection";
    private const string ReceiverProcess = "Receiver";

    private static readonly object Gate = new();
    private static (IntPtr Frame, ScreenRect Rect)? _beforeFill;

    /// <summary>The frame window and the area the picture is shown in.</summary>
    public readonly record struct Found(IntPtr Frame, ScreenRect FrameRect, ScreenRect Content, bool Minimized);

    /// <summary>Opens the app if it has no window; never closes it.</summary>
    public static bool EnsureOpen(Action<string> log)
    {
        if (Find() is not null || Process.GetProcessesByName(ReceiverProcess).Length > 0) return true;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{AppId}") { UseShellExecute = false })?.Dispose();
            log("wireless display: opened the Wireless Display app");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"wireless display: could not open it ({ex.Message})");
            return false;
        }
    }

    /// <summary>The Wireless Display window, or null if it is not open.</summary>
    public static Found? Find()
    {
        Found? result = null;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || ClassOf(window) != "ApplicationFrameWindow") return true;
            if (FindChild(window, "Windows.UI.Core.CoreWindow", ReceiverProcess) is not { } core) return true;
            if (!GetWindowRect(window, out var frame) || !GetWindowRect(core, out var coreRect)) return true;

            // The title bar covers the top of the CoreWindow; the input sink starts below it.
            var top = coreRect.Top;
            if (FindChild(window, "ApplicationFrameInputSinkWindow", null) is { } sink
                && IsWindowVisible(sink) && GetWindowRect(sink, out var sinkRect)
                && sinkRect.Top > coreRect.Top && sinkRect.Top < coreRect.Bottom)
                top = sinkRect.Top;

            result = new Found(window, frame.ToScreen(),
                new ScreenRect(coreRect.Left, top, coreRect.Right, coreRect.Bottom), IsIconic(window));
            return false;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Where the frame has to go for its picture area to cover the monitor exactly: the title bar
    /// and the invisible resize borders end up just off screen.
    /// </summary>
    public static ScreenRect FillTarget(ScreenRect frame, ScreenRect content, ScreenRect monitor) => new(
        monitor.Left - (content.Left - frame.Left),
        monitor.Top - (content.Top - frame.Top),
        monitor.Right + (frame.Right - content.Right),
        monitor.Bottom + (frame.Bottom - content.Bottom));

    /// <summary>
    /// Fills the monitor with the picture, or puts the window back where it was if PhoneDeck
    /// filled it. Returns what happened, for the log.
    /// </summary>
    public static string ToggleFill()
    {
        lock (Gate)
        {
            if (Find() is not { Minimized: false } found) return "no Wireless Display window to fill";

            if (_beforeFill is { } saved && saved.Frame == found.Frame)
            {
                _beforeFill = null;
                Place(found.Frame, saved.Rect, IntPtr.Zero);
                return "window size restored";
            }

            // The work area, not the whole monitor: the taskbar stays on top of ordinary windows,
            // so the picture and the bar are kept above it.
            var monitor = WorkAreaOf(found.Frame);
            // Only an exact fit counts: a window filled over the taskbar, or by an older build, is refitted.
            if (found.Content == monitor) return "the picture already fills the screen";

            // A maximised window ignores a new size, and restoring is not minimising.
            if (IsZoomed(found.Frame))
            {
                ShowWindow(found.Frame, SW_RESTORE);
                if (Find() is { } restored) found = restored;
            }

            _beforeFill = (found.Frame, found.FrameRect);
            Place(found.Frame, FillTarget(found.FrameRect, found.Content, monitor), HWND_TOP);
            return "picture fills the screen";
        }
    }

    /// <summary>Undoes a fill, so the app does not remember a size with its title bar off screen.</summary>
    public static void RestoreIfFilled()
    {
        lock (Gate)
        {
            if (_beforeFill is not { } saved) return;
            _beforeFill = null;
            if (Find() is { Minimized: false } found && found.Frame == saved.Frame) Place(saved.Frame, saved.Rect, IntPtr.Zero);
        }
    }

    /// <summary>Puts the window behind all others: unlike minimising, this keeps the cast going.</summary>
    public static bool SendToBack()
    {
        if (Find() is not { Minimized: false } found) return false;
        return SetWindowPos(found.Frame, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static void Place(IntPtr window, ScreenRect rect, IntPtr insertAfter)
    {
        var flags = SWP_NOACTIVATE | (insertAfter == IntPtr.Zero ? SWP_NOZORDER : 0);
        SetWindowPos(window, insertAfter, rect.Left, rect.Top, rect.Width, rect.Height, flags);
    }

    private static ScreenRect WorkAreaOf(IntPtr window)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
            ? info.rcWork.ToScreen()
            : new ScreenRect(0, 0, ScreenMetrics.PrimaryWidth, ScreenMetrics.PrimaryHeight);
    }

    private static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(64);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static IntPtr? FindChild(IntPtr parent, string className, string? processName)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (child, _) =>
        {
            if (ClassOf(child) != className) return true;
            if (processName is not null && !IsProcess(child, processName)) return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : found;
    }

    private static bool IsProcess(IntPtr window, string name)
    {
        GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const int SW_RESTORE = 9;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public ScreenRect ToScreen() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}

/// <summary>
/// Keeps <see cref="VideoRegion"/> on the picture in the Wireless Display window, the way
/// <see cref="MpvVideoLocator"/> does for mpv. The cast's picture is assumed to be 16:9,
/// letterboxed in the area below the title bar.
/// </summary>
public sealed class WirelessDisplayLocator : IDisposable
{
    private const int PollMs = 400;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private string _lastNote = "";

    public WirelessDisplayLocator(Action<string> log)
    {
        _log = log;
        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>The picture inside the content area, or null when there is no usable window.</summary>
    public static VideoRect? Locate(WirelessDisplay.Found? found) =>
        found is { Minimized: false, Content: { Width: > 0, Height: > 0 } content }
            ? VideoRect.Letterbox(content.Left, content.Top, content.Width, content.Height, TouchMapper.DefaultVideoAspect)
            : null;

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var found = WirelessDisplay.Find();
                var picture = Locate(found);
                VideoRegion.Current = picture;
                Note(picture is null ? "no Wireless Display window; assuming a full-screen 16:9 picture" : "Wireless Display window");
            }
            catch (Exception ex)
            {
                Note($"Wireless Display lookup failed: {ex.Message}");
            }

            try { await Task.Delay(PollMs, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Note(string message)
    {
        if (message == _lastNote) return;
        _lastNote = message;
        _log(message);
    }

    public void Dispose()
    {
        _stop.Cancel();
        VideoRegion.Current = null;
    }
}
