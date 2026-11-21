using System.Runtime.InteropServices;

namespace PhoneDeck.Core;

/// <summary>Where the phone's picture is, in physical pixels of the main display.</summary>
public readonly record struct VideoRect(double Left, double Top, double Width, double Height)
{
    public bool Contains(double x, double y) =>
        x >= Left && x <= Left + Width && y >= Top && y <= Top + Height;

    /// <summary>A video of the given aspect, letterboxed and centred inside an area.</summary>
    public static VideoRect Letterbox(double left, double top, double width, double height, double videoAspect)
    {
        if (width <= 0 || height <= 0) return new VideoRect(left, top, Math.Max(0, width), Math.Max(0, height));

        if (width / height > videoAspect)
        {
            var videoWidth = height * videoAspect;
            return new VideoRect(left + (width - videoWidth) / 2, top, videoWidth, height);
        }

        var videoHeight = width / videoAspect;
        return new VideoRect(left, top + (height - videoHeight) / 2, width, videoHeight);
    }
}

/// <summary>
/// The video's current position. The viewer locator updates it as the mpv window moves or
/// resizes; until it knows better it is a full-screen 16:9 picture on the main display, which is
/// what PhoneDeck always assumed before.
/// </summary>
public static class VideoRegion
{
    // A nullable struct is not written atomically, and the hook thread reads it.
    private static readonly object Gate = new();
    private static VideoRect? _current;

    /// <summary>Null means unknown: use the full-screen default.</summary>
    public static VideoRect? Current
    {
        get { lock (Gate) return _current; }
        set
        {
            lock (Gate)
            {
                if (Nullable.Equals(_current, value)) return;
                _current = value;
            }
            Changed?.Invoke();
        }
    }

    public static event Action? Changed;

    private static VideoRect? _excluded;

    /// <summary>The button bar, when it has to sit over the video: touches there are its own.</summary>
    public static VideoRect? Excluded
    {
        get { lock (Gate) return _excluded; }
        set { lock (Gate) _excluded = value; }
    }

    public static VideoRect Resolve() => Current ?? FullScreen();

    /// <summary>Whether a point is on the phone's picture and not on the button bar.</summary>
    public static bool IsOnVideo(double x, double y)
    {
        if (!Resolve().Contains(x, y)) return false;
        return Excluded is not { } bar || !bar.Contains(x, y);
    }

    public static VideoRect FullScreen() => VideoRect.Letterbox(0, 0,
        ScreenMetrics.PrimaryWidth, ScreenMetrics.PrimaryHeight, TouchMapper.DefaultVideoAspect);
}

/// <summary>The main display's size in physical pixels; the process is per-monitor DPI aware.</summary>
public static class ScreenMetrics
{
    public static int PrimaryWidth => GetSystemMetrics(0);
    public static int PrimaryHeight => GetSystemMetrics(1);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

/// <summary>
/// Maps a point on the screen to touch coordinates on the video.
/// </summary>
public static class TouchMapper
{
    public const double DefaultVideoAspect = 16.0 / 9.0;

    public static bool IsInsideVideo(double x, double y, double viewWidth, double viewHeight, double videoAspect) =>
        VideoRect.Letterbox(0, 0, viewWidth, viewHeight, videoAspect).Contains(x, y);

    /// <summary>Clamped, so a finger dragged into the black bars stays on the video's edge.</summary>
    public static (int X, int Y) Map(double x, double y, double viewWidth, double viewHeight, double videoAspect) =>
        Map(x, y, VideoRect.Letterbox(0, 0, viewWidth, viewHeight, videoAspect));

    /// <summary>Clamped, so a finger dragged off the video stays on the video's edge.</summary>
    public static (int X, int Y) Map(double x, double y, VideoRect video)
    {
        if (video.Width <= 0 || video.Height <= 0) return (0, 0);
        return (Scale((x - video.Left) / video.Width), Scale((y - video.Top) / video.Height));
    }

    private static int Scale(double fraction) =>
        (int)Math.Round(Math.Clamp(fraction, 0, 1) * HidReports.TouchLogicalMax);
}
