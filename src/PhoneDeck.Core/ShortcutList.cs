namespace PhoneDeck.Core;

/// <summary>One line of the Details page's shortcut list: what to press, and what it does.</summary>
public readonly record struct Shortcut(string Keys, string Does);

/// <summary>
/// The shortcuts that work with the current settings, so Details never shows a key that is off
/// or a hotkey that was changed.
/// </summary>
public static class ShortcutList
{
    /// <param name="hasPicture">False for keyboard and mouse only: no picture, bar or viewer.</param>
    public static IReadOnlyList<Shortcut> Build(string hotkey, bool clickPicture, bool ctrlAltTap,
        bool copilotKey, bool touchOutside, bool builtInViewer, bool hasPicture = true)
    {
        var list = new List<Shortcut> { new(hotkey, "Give the phone the keyboard and mouse, or take them back") };
        if (hasPicture && clickPicture) list.Add(new("Click or touch the phone's picture", "Give the phone control"));
        if (ctrlAltTap) list.Add(new("Tap Ctrl+Alt on their own", "Back to this PC"));
        if (copilotKey) list.Add(new("Copilot key", "Back to this PC"));
        if (!hasPicture) return list;
        if (touchOutside) list.Add(new("Touch outside the picture", "Back to this PC"));
        list.Add(new("The bar's full-screen button", "Full screen or a window"));
        if (builtInViewer) list.Add(new("F11 or double-click the picture", "Full screen or a window, while this PC has control"));
        return list;
    }
}
