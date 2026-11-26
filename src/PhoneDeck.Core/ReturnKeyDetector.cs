namespace PhoneDeck.Core;

/// <summary>
/// Recognises the one-press ways back to this PC while input goes to the phone. Each one
/// completes when the last key comes up, so every key the gesture pressed is released while
/// the keyboard hook is still there to keep the releases away from Windows.
/// <list type="bullet">
/// <item>Ctrl+Alt tapped with no other key: every keyboard has both, and on the phone they do
/// nothing on their own. A third key (Ctrl+Alt+Del, Ctrl+Alt+T) or a mouse button cancels it.</item>
/// <item>The Copilot key, which Windows reports as Win+Shift+F23. Not every keyboard has one.</item>
/// </list>
/// </summary>
public sealed class ReturnKeyDetector
{
    public const int LeftShift = 0xA0, RightShift = 0xA1;
    public const int LeftControl = 0xA2, RightControl = 0xA3;
    public const int LeftAlt = 0xA4, RightAlt = 0xA5;
    public const int LeftWin = 0x5B, RightWin = 0x5C;
    public const int F23 = 0x86;

    /// <summary>
    /// AltGr on European layouts arrives as a made-up Left Ctrl (this scan code) plus Right Alt.
    /// Tapping AltGr is typing, not Ctrl+Alt, so that Ctrl never counts.
    /// </summary>
    public const uint AltGrControlScanCode = 0x21D;

    private readonly HashSet<int> _held = [];
    private bool _sawControl, _sawAlt, _onlyControlAlt = true;
    private bool _sawCopilot, _onlyCopilotKeys = true;
    private bool _spoiled;

    public bool CtrlAltTap { get; set; } = true;
    public bool CopilotKey { get; set; } = true;

    /// <summary>Whether this key is the Copilot key's F23, with the Win it always comes with.</summary>
    public bool IsCopilotPress(int virtualKey, bool isDown) =>
        CopilotKey && isDown && virtualKey == F23 && (_held.Contains(LeftWin) || _held.Contains(RightWin));

    /// <summary>
    /// Feeds one key event. True when it completed a way back: the caller then returns input to
    /// this PC. Releases of keys pressed before the detector saw them are ignored.
    /// </summary>
    public bool Feed(int virtualKey, bool isDown, uint scanCode = 0)
    {
        if (isDown)
        {
            if (_held.Count == 0) StartSequence();

            // Classified before it is added, so F23 can see the Win already held.
            var isControl = virtualKey is LeftControl or RightControl && scanCode != AltGrControlScanCode;
            var isAlt = virtualKey is LeftAlt or RightAlt;
            var isCopilotPart = virtualKey is LeftWin or RightWin or LeftShift or RightShift ||
                                (virtualKey == F23 && (_held.Contains(LeftWin) || _held.Contains(RightWin)));

            if (isControl) _sawControl = true;
            else if (isAlt) _sawAlt = true;
            else _onlyControlAlt = false;

            if (virtualKey == F23 && isCopilotPart) _sawCopilot = true;
            if (!isCopilotPart) _onlyCopilotKeys = false;

            _held.Add(virtualKey);
            return false;
        }

        if (!_held.Remove(virtualKey) || _held.Count > 0) return false;

        var completed = !_spoiled && (
            CtrlAltTap && _sawControl && _sawAlt && _onlyControlAlt ||
            CopilotKey && _sawCopilot && _onlyCopilotKeys);
        StartSequence();
        return completed;
    }

    /// <summary>A mouse button or the wheel used meanwhile: Ctrl+Alt+click is not a tap.</summary>
    public void Spoil()
    {
        if (_held.Count > 0) _spoiled = true;
    }

    /// <summary>Forgets everything, as when the target changes.</summary>
    public void Reset()
    {
        _held.Clear();
        StartSequence();
    }

    private void StartSequence()
    {
        _sawControl = _sawAlt = _sawCopilot = _spoiled = false;
        _onlyControlAlt = _onlyCopilotKeys = true;
    }
}
