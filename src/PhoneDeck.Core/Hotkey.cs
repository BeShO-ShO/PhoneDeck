namespace PhoneDeck.Core;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8
}

/// <summary>
/// The key combination that turns capture on and off, Ctrl+Alt+Q unless changed in Settings.
/// The modifier flags use RegisterHotKey's own values, so they can be passed to it unchanged.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static readonly Hotkey Default = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x51);

    /// <summary>
    /// Needs Ctrl, Alt or Win and one ordinary key. Shift alone would take a capital letter
    /// away from typing, and a modifier on its own is not a key Windows can register.
    /// </summary>
    public bool IsValid =>
        (Modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0 &&
        KeyName(VirtualKey) is not null;

    /// <summary>Whether a key going down, with exactly these modifiers held, is this hotkey.</summary>
    public bool Matches(int virtualKey, HotkeyModifiers held) => virtualKey == VirtualKey && held == Modifiers;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey) ?? $"0x{VirtualKey:X2}");
        return string.Join('+', parts);
    }

    /// <summary>Reads "Ctrl+Alt+Q" and the like; anything not valid gives the default.</summary>
    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Default;

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= HotkeyModifiers.Ctrl; continue;
                case "ALT": modifiers |= HotkeyModifiers.Alt; continue;
                case "SHIFT": modifiers |= HotkeyModifiers.Shift; continue;
                case "WIN" or "WINDOWS": modifiers |= HotkeyModifiers.Win; continue;
            }

            // A second ordinary key is not something one hotkey can be.
            if (key is not null || KeyFromName(raw) is not { } found) return Default;
            key = found;
        }

        var hotkey = new Hotkey(modifiers, key ?? 0);
        return hotkey.IsValid ? hotkey : Default;
    }

    /// <summary>The keys a hotkey can end in: letters, digits, F1 to F12 and a few others.</summary>
    public static string? KeyName(int virtualKey) => virtualKey switch
    {
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x7B => $"F{virtualKey - 0x6F}",
        0x20 => "Space",
        0x13 => "Pause",
        0x91 => "ScrollLock",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0xC0 => "`",
        _ => null
    };

    private static int? KeyFromName(string name)
    {
        for (var key = 0x01; key <= 0xFE; key++)
            if (string.Equals(KeyName(key), name, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }
}
