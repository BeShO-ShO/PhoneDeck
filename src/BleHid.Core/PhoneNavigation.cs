namespace BleHid.Core;

public readonly record struct KeyTap(KeyModifiers Modifiers, byte Usage);

/// <summary>
/// Android's system navigation reached through the keyboard, since edge gestures belong to the
/// phone's own screen and an external touch device cannot perform them.
/// </summary>
public static class PhoneNavigation
{
    private const byte Escape = 0x29;
    private const byte H = 0x0B;
    private const byte Tab = 0x2B;
    private const byte Backspace = 0x2A;
    private const byte LeftArrow = 0x50;
    private const byte Grave = 0x35;

    // Plain Escape did not reach Back in DeX. These are separate on purpose: unlike Home, going
    // Back twice is not harmless, so candidates cannot be sent together.
    public static readonly KeyTap[] BackA = [new(KeyModifiers.LeftGui, Backspace)];
    public static readonly KeyTap[] BackB = [new(KeyModifiers.LeftGui, LeftArrow)];
    public static readonly KeyTap[] BackC = [new(KeyModifiers.LeftGui, Grave)];

    // Two routes because Android versions and DeX differ on which one reaches Home. Going Home
    // twice is harmless, so both are sent.
    public static readonly KeyTap[] Home =
    [
        new(KeyModifiers.LeftGui, H),
        new(KeyModifiers.LeftGui, Escape)
    ];

    public static readonly KeyTap[] Recents = [new(KeyModifiers.LeftGui, Tab)];
}
