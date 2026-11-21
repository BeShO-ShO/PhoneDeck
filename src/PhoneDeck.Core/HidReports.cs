namespace PhoneDeck.Core;

[Flags]
public enum KeyModifiers : byte
{
    None         = 0x00,
    LeftControl  = 0x01,
    LeftShift    = 0x02,
    LeftAlt      = 0x04,
    LeftGui      = 0x08,
    RightControl = 0x10,
    RightShift   = 0x20,
    RightAlt     = 0x40,
    RightGui     = 0x80
}

[Flags]
public enum MouseButtons : byte
{
    None   = 0x00,
    Left   = 0x01,
    Right  = 0x02,
    Middle = 0x04
}

/// <summary>
/// Lock keys whose state the host owns rather than the keyboard. Values match the HID LED
/// report bit positions, so a report byte casts straight across. Scroll Lock is deliberately
/// left out: nothing on a phone reacts to it, so reporting it would only add noise.
/// </summary>
[Flags]
public enum LockKeys : byte
{
    None     = 0x00,
    NumLock  = 0x01,
    CapsLock = 0x02
}

/// <summary>Pen state bits, in the order the pen collection declares them.</summary>
[Flags]
public enum PenFlags : byte
{
    None    = 0x00,
    Tip     = 0x01,
    Barrel  = 0x02,
    /// <summary>The eraser end is the one near the screen.</summary>
    Invert  = 0x04,
    /// <summary>The eraser end is pressed on the screen.</summary>
    Eraser  = 0x08,
    InRange = 0x10
}

/// <summary>One finger in a touch frame, in touch logical units.</summary>
public readonly record struct TouchPoint(int ContactId, bool Down, int X, int Y);

/// <summary>
/// Builds HID input report payloads. In HOGP report-protocol mode the Report ID is
/// conveyed by the characteristic's Report Reference descriptor, not the payload.
/// </summary>
public static class HidReports
{
    public const int KeyboardReportLength = 8;
    public const int MouseReportLength    = 6;
    public const int TouchReportLength    = 7;
    public const int PenReportLength      = 7;
    public const int TouchLogicalMax      = 32767;
    public const int PenPressureMax       = 4095;

    /// <summary>
    /// The touch report carries one finger, so a frame of several fingers goes out as several
    /// reports ("hybrid" mode): the first says how many fingers the frame has, the rest say 0,
    /// and the host waits for them all before it updates the screen.
    /// </summary>
    public const int MaxTouchContacts = 5;

    /// <summary>One contact: flags, contact id, X, Y, contact count.</summary>
    public static byte[] Touch(bool down, int x, int y) => Touch(down, 0, x, y, 1);

    /// <summary>One contact of a frame. <paramref name="contactCount"/> is the frame size on its
    /// first report and 0 on the ones that follow.</summary>
    public static byte[] Touch(bool down, int contactId, int x, int y, int contactCount)
    {
        var clampedX = Math.Clamp(x, 0, TouchLogicalMax);
        var clampedY = Math.Clamp(y, 0, TouchLogicalMax);
        return
        [
            (byte)(down ? 0x03 : 0x00), // tip switch + in range
            (byte)Math.Clamp(contactId, 0, 127),
            (byte)(clampedX & 0xFF), (byte)((clampedX >> 8) & 0xFF),
            (byte)(clampedY & 0xFF), (byte)((clampedY >> 8) & 0xFF),
            (byte)Math.Clamp(contactCount, 0, 127)
        ];
    }

    /// <summary>The reports for one frame: every finger down plus any lifting in this frame.</summary>
    public static List<byte[]> TouchFrame(IReadOnlyList<TouchPoint> contacts)
    {
        var reports = new List<byte[]>(contacts.Count);
        for (var i = 0; i < contacts.Count; i++)
        {
            var contact = contacts[i];
            reports.Add(Touch(contact.Down, contact.ContactId, contact.X, contact.Y, i == 0 ? contacts.Count : 0));
        }
        return reports;
    }

    /// <summary>Pen: state bits, X, Y, tip pressure.</summary>
    public static byte[] Pen(PenFlags flags, int x, int y, int pressure)
    {
        var clampedX = Math.Clamp(x, 0, TouchLogicalMax);
        var clampedY = Math.Clamp(y, 0, TouchLogicalMax);
        var touching = (flags & (PenFlags.Tip | PenFlags.Eraser)) != 0;
        var clampedPressure = touching ? Math.Clamp(pressure, 0, PenPressureMax) : 0;
        return
        [
            (byte)flags,
            (byte)(clampedX & 0xFF), (byte)((clampedX >> 8) & 0xFF),
            (byte)(clampedY & 0xFF), (byte)((clampedY >> 8) & 0xFF),
            (byte)(clampedPressure & 0xFF), (byte)((clampedPressure >> 8) & 0xFF)
        ];
    }

    public static byte[] Keyboard(KeyModifiers modifiers, params byte[] usages)
    {
        var report = new byte[KeyboardReportLength];
        report[0] = (byte)modifiers;
        var count = Math.Min(usages.Length, 6);
        for (var i = 0; i < count; i++)
            report[2 + i] = usages[i];
        return report;
    }

    public static byte[] KeyboardRelease() => new byte[KeyboardReportLength];

    public static byte[] Mouse(MouseButtons buttons, int dx, int dy, int wheel)
    {
        var x = (short)Math.Clamp(dx, -32767, 32767);
        var y = (short)Math.Clamp(dy, -32767, 32767);
        return
        [
            (byte)buttons,
            (byte)(x & 0xFF), (byte)((x >> 8) & 0xFF),
            (byte)(y & 0xFF), (byte)((y >> 8) & 0xFF),
            unchecked((byte)(sbyte)Math.Clamp(wheel, -127, 127))
        ];
    }

    /// <summary>Maps a printable ASCII character to its HID usage ID and required modifiers.</summary>
    public static bool TryMapChar(char c, out byte usage, out KeyModifiers modifiers)
    {
        modifiers = KeyModifiers.None;
        usage = 0;

        switch (c)
        {
            case >= 'a' and <= 'z':
                usage = (byte)(0x04 + (c - 'a'));
                return true;
            case >= 'A' and <= 'Z':
                usage = (byte)(0x04 + (c - 'A'));
                modifiers = KeyModifiers.LeftShift;
                return true;
            case >= '1' and <= '9':
                usage = (byte)(0x1E + (c - '1'));
                return true;
            case '0': usage = 0x27; return true;
            case '\n': usage = 0x28; return true; // Enter
            case '\t': usage = 0x2B; return true; // Tab
            case ' ':  usage = 0x2C; return true;
        }

        // Symbols that share a key with an unshifted counterpart.
        const string unshifted = "-=[]\\;'`,./";
        var index = unshifted.IndexOf(c);
        if (index >= 0)
        {
            usage = UnshiftedUsages[index];
            return true;
        }

        const string shifted = "_+{}|:\"~<>?";
        index = shifted.IndexOf(c);
        if (index >= 0)
        {
            usage = UnshiftedUsages[index];
            modifiers = KeyModifiers.LeftShift;
            return true;
        }

        const string shiftedDigits = "!@#$%^&*()";
        index = shiftedDigits.IndexOf(c);
        if (index >= 0)
        {
            usage = index == 9 ? (byte)0x27 : (byte)(0x1E + index);
            modifiers = KeyModifiers.LeftShift;
            return true;
        }

        return false;
    }

    private static readonly byte[] UnshiftedUsages =
        [0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38];

    public static readonly IReadOnlyDictionary<string, byte> NamedKeys =
        new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            ["enter"] = 0x28, ["esc"] = 0x29, ["escape"] = 0x29, ["backspace"] = 0x2A,
            ["tab"] = 0x2B, ["space"] = 0x2C, ["capslock"] = 0x39,
            ["f1"] = 0x3A, ["f2"] = 0x3B, ["f3"] = 0x3C, ["f4"] = 0x3D,
            ["f5"] = 0x3E, ["f6"] = 0x3F, ["f7"] = 0x40, ["f8"] = 0x41,
            ["f9"] = 0x42, ["f10"] = 0x43, ["f11"] = 0x44, ["f12"] = 0x45,
            ["home"] = 0x4A, ["pageup"] = 0x4B, ["delete"] = 0x4C, ["end"] = 0x4D,
            ["pagedown"] = 0x4E, ["right"] = 0x4F, ["left"] = 0x50,
            ["down"] = 0x51, ["up"] = 0x52
        };
}
