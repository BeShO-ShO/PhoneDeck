namespace PhoneDeck.Core;

/// <summary>
/// The tray icon's colour while the phone has control: its blues become the red of the bar's
/// "live" dot, and everything else (white, the orange key) stays as it is.
/// </summary>
public static class LiveTint
{
    public static (byte R, byte G, byte B) Apply(byte r, byte g, byte b)
    {
        // Blue-dominant only: the white phone outline and the orange key keep their colour.
        if (b <= r + 40 || b <= g) return (r, g, b);
        return (b, (byte)(g * 2 / 5), r);
    }
}
