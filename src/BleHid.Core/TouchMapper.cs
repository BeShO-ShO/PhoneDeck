namespace BleHid.Core;

/// <summary>
/// Maps a point on a full-screen view to touch coordinates on a video letterboxed inside it.
/// </summary>
public static class TouchMapper
{
    public static bool IsInsideVideo(double x, double y, double viewWidth, double viewHeight, double videoAspect)
    {
        var (left, top, width, height) = VideoRect(viewWidth, viewHeight, videoAspect);
        return x >= left && x <= left + width && y >= top && y <= top + height;
    }

    /// <summary>Clamped, so a finger dragged into the black bars stays on the video's edge.</summary>
    public static (int X, int Y) Map(double x, double y, double viewWidth, double viewHeight, double videoAspect)
    {
        var (left, top, width, height) = VideoRect(viewWidth, viewHeight, videoAspect);
        return (Scale((x - left) / width), Scale((y - top) / height));
    }

    private static int Scale(double fraction) =>
        (int)Math.Round(Math.Clamp(fraction, 0, 1) * HidReports.TouchLogicalMax);

    private static (double Left, double Top, double Width, double Height) VideoRect(
        double viewWidth, double viewHeight, double videoAspect)
    {
        if (viewWidth / viewHeight > videoAspect)
        {
            var width = viewHeight * videoAspect;
            return ((viewWidth - width) / 2, 0, width, viewHeight);
        }

        var height = viewWidth / videoAspect;
        return (0, (viewHeight - height) / 2, viewWidth, height);
    }
}
