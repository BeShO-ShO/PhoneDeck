namespace PhoneDeck.Core;

/// <summary>How the phone's screen reaches this PC.</summary>
public enum DisplayRoute
{
    /// <summary>The phone's video output through a USB capture device.</summary>
    CaptureDevice = 0,

    /// <summary>DeX cast over Wi-Fi to PhoneDeck's own receiver.</summary>
    Cast = 1,

    /// <summary>DeX cast to Windows' Wireless Display app, the backup for <see cref="Cast"/>.</summary>
    WirelessDisplayApp = 2,

    /// <summary>
    /// No picture on this PC: PhoneDeck is only the phone's Bluetooth keyboard and mouse, for DeX
    /// on a separate monitor or TV.
    /// </summary>
    KeyboardOnly = 3,
}

public static class DisplayRoutes
{
    /// <summary>
    /// The route from settings. Older settings only had a yes/no for the Wireless Display app;
    /// a yes now means PhoneDeck's own receiver, which replaced the app as the wireless choice.
    /// </summary>
    public static DisplayRoute FromSaved(int? saved, bool legacyWireless) => saved switch
    {
        >= (int)DisplayRoute.CaptureDevice and <= (int)DisplayRoute.KeyboardOnly => (DisplayRoute)saved.Value,
        _ => legacyWireless ? DisplayRoute.Cast : DisplayRoute.CaptureDevice,
    };

    /// <summary>Home offers one Wireless choice; Settings picks which receiver.</summary>
    public static bool IsWireless(DisplayRoute route) =>
        route is DisplayRoute.Cast or DisplayRoute.WirelessDisplayApp;

    /// <summary>Whether the phone's screen is shown on this PC at all.</summary>
    public static bool ShowsPicture(DisplayRoute route) => route != DisplayRoute.KeyboardOnly;

    /// <summary>The wireless route for the receiver chosen in Settings.</summary>
    public static DisplayRoute Wireless(bool useApp) => useApp ? DisplayRoute.WirelessDisplayApp : DisplayRoute.Cast;

    /// <summary>
    /// The receiver Settings shows as chosen: the saved choice, or for settings saved before it
    /// existed, whichever the saved route already uses.
    /// </summary>
    public static bool UsesApp(bool? saved, DisplayRoute route) => saved ?? route == DisplayRoute.WirelessDisplayApp;

    /// <summary>
    /// PhoneDeck sends touch unless the Wireless Display app is showing the phone: its cast carries
    /// touch itself, and sending it too would double every touch.
    /// </summary>
    public static bool SendsTouch(DisplayRoute route, bool touchLayer) =>
        route is DisplayRoute.CaptureDevice or DisplayRoute.Cast && touchLayer;

    /// <summary>
    /// The app's cast ignores the pen, so PhoneDeck always sends it there. With no picture, the
    /// PC's screen doesn't line up with the phone's, so neither touch nor pen is sent.
    /// </summary>
    public static bool SendsPen(DisplayRoute route, bool touchLayer) =>
        route == DisplayRoute.WirelessDisplayApp || (route != DisplayRoute.KeyboardOnly && touchLayer);
}

public enum CastState
{
    Off,
    Starting,
    Waiting,
    Connected,
    Unsupported,
    Failed,
}

/// <summary>What PhoneDeck's cast receiver is doing, as Home shows it.</summary>
public readonly record struct CastStatus(CastState State, string? Phone = null, string? Problem = null)
{
    public string Describe() => State switch
    {
        CastState.Off => "Not receiving.",
        CastState.Starting => "Getting ready to receive the cast...",
        CastState.Waiting => "Ready: cast DeX to this PC from the phone.",
        CastState.Connected => $"Showing {(string.IsNullOrWhiteSpace(Phone) ? "the phone" : Phone)}.",
        CastState.Unsupported =>
            "This PC's Wi-Fi can't receive a cast. Use the Wireless Display app or a capture device instead.",
        _ => $"Couldn't receive the cast{(string.IsNullOrWhiteSpace(Problem) ? "" : $" ({Problem})")}. Try the Wireless Display app instead.",
    };

    /// <summary>Whether Home should show it as a problem.</summary>
    public bool IsProblem => State is CastState.Unsupported or CastState.Failed;
}
