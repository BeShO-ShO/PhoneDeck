namespace PhoneDeck.Core;

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

    // Tested in DeX: plain Escape and Win+` do nothing, Win+Backspace is a normal Back.
    public static readonly KeyTap[] Back = [new(KeyModifiers.LeftGui, Backspace)];

    // Tested in DeX: moves the current app to the left and offers another app for the right.
    public static readonly KeyTap[] SplitScreen = [new(KeyModifiers.LeftGui, LeftArrow)];

    // Two routes because Android versions and DeX differ on which one reaches Home. Going Home
    // twice is harmless, so both are sent.
    public static readonly KeyTap[] Home =
    [
        new(KeyModifiers.LeftGui, H),
        new(KeyModifiers.LeftGui, Escape)
    ];

    public static readonly KeyTap[] Recents = [new(KeyModifiers.LeftGui, Tab)];
}
