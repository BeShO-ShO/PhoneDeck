using System.Globalization;

namespace PhoneDeck.Core;

/// <summary>
/// Where the viewer window last was when not full screen, in physical pixels, saved as
/// "left,top,width,height" so the next window opens there instead of at the default size.
/// </summary>
public readonly record struct WindowPlacement(int Left, int Top, int Width, int Height)
{
    /// <summary>Smaller than this is treated as a mistake rather than a size to remember.</summary>
    public const int MinWidth = 320, MinHeight = 180;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Left},{Top},{Width},{Height}");

    /// <summary>Null for nothing saved, unreadable text, or a size too small to use.</summary>
    public static WindowPlacement? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(',');
        if (parts.Length != 4) return null;
        var values = new int[4];
        for (var i = 0; i < 4; i++)
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                return null;
        var placement = new WindowPlacement(values[0], values[1], values[2], values[3]);
        return placement.Width >= MinWidth && placement.Height >= MinHeight ? placement : null;
    }

    /// <summary>
    /// Whether enough of the window (its title bar's middle) lands on a screen to grab it: a
    /// monitor unplugged since it was saved would otherwise leave the window out of reach.
    /// </summary>
    public bool TitleIsOnScreen(Func<int, int, bool> isOnAMonitor) =>
        isOnAMonitor(Left + Width / 2, Top + 10);
}
