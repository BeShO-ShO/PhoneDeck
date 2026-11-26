using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public sealed class SwitchingTests
{
    private const int LeftControl = 0xA2, RightControl = 0xA3, LeftAlt = 0xA4, RightAlt = 0xA5;
    private const int LeftShift = 0xA0, LeftWin = 0x5B, F23 = 0x86;

    // Hotkey

    [Theory]
    [InlineData("Ctrl+Alt+Q", "Ctrl+Alt+Q")]
    [InlineData("ctrl + alt + q", "Ctrl+Alt+Q")]
    [InlineData("Alt+Ctrl+F9", "Ctrl+Alt+F9")]
    [InlineData("Ctrl+Shift+Space", "Ctrl+Shift+Space")]
    [InlineData("Win+Alt+1", "Alt+Win+1")]
    public void Hotkeys_read_and_write_the_same_way(string text, string expected) =>
        Assert.Equal(expected, Hotkey.Parse(text).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Q")]
    [InlineData("Shift+Q")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Q+W")]
    [InlineData("Ctrl+Banana")]
    public void Anything_that_cannot_be_a_hotkey_gives_ctrl_alt_q(string? text) =>
        Assert.Equal(Hotkey.Default, Hotkey.Parse(text));

    [Fact]
    public void The_default_is_ctrl_alt_q_with_register_hotkey_flags()
    {
        Assert.Equal("Ctrl+Alt+Q", Hotkey.Default.ToString());
        // MOD_ALT | MOD_CONTROL, as RegisterHotKey takes them.
        Assert.Equal(3, (int)Hotkey.Default.Modifiers);
        Assert.Equal(0x51, Hotkey.Default.VirtualKey);
    }

    [Fact]
    public void A_hotkey_matches_only_its_exact_modifiers()
    {
        var hotkey = Hotkey.Parse("Ctrl+Alt+Q");
        Assert.True(hotkey.Matches(0x51, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt));
        Assert.False(hotkey.Matches(0x51, HotkeyModifiers.Ctrl));
        Assert.False(hotkey.Matches(0x51, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift));
        Assert.False(hotkey.Matches(0x57, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt));
    }

    // Tapping Ctrl+Alt

    private static bool Tap(ReturnKeyDetector detector, params (int Key, bool Down)[] events)
    {
        var completed = false;
        foreach (var (key, down) in events) completed |= detector.Feed(key, down);
        return completed;
    }

    [Theory]
    [InlineData(LeftControl, LeftAlt)]
    [InlineData(RightControl, RightAlt)]
    [InlineData(LeftAlt, LeftControl)]
    public void Tapping_ctrl_alt_alone_returns_when_the_last_key_comes_up(int first, int second)
    {
        var detector = new ReturnKeyDetector();
        Assert.False(detector.Feed(first, true));
        Assert.False(detector.Feed(second, true));
        Assert.False(detector.Feed(first, false));
        Assert.True(detector.Feed(second, false));
    }

    [Fact]
    public void Key_repeat_while_holding_ctrl_alt_still_counts_as_a_tap()
    {
        var detector = new ReturnKeyDetector();
        Assert.True(Tap(detector, (LeftControl, true), (LeftAlt, true), (LeftControl, true), (LeftAlt, true),
            (LeftAlt, false), (LeftControl, false)));
    }

    [Theory]
    [InlineData(0x2E)] // Ctrl+Alt+Del
    [InlineData(0x54)] // Ctrl+Alt+T
    [InlineData(0x14)] // Ctrl+Alt+CapsLock
    [InlineData(LeftShift)]
    public void A_third_key_cancels_the_tap(int third)
    {
        var detector = new ReturnKeyDetector();
        Assert.False(Tap(detector, (LeftControl, true), (LeftAlt, true), (third, true), (third, false),
            (LeftAlt, false), (LeftControl, false)));
    }

    [Fact]
    public void Ctrl_then_alt_one_after_the_other_is_not_a_tap()
    {
        var detector = new ReturnKeyDetector();
        Assert.False(Tap(detector, (LeftControl, true), (LeftControl, false), (LeftAlt, true), (LeftAlt, false)));
    }

    [Fact]
    public void Ctrl_or_alt_alone_is_not_a_tap()
    {
        var detector = new ReturnKeyDetector();
        Assert.False(Tap(detector, (LeftControl, true), (LeftControl, false)));
        Assert.False(Tap(detector, (RightAlt, true), (RightAlt, false)));
    }

    [Fact]
    public void A_mouse_click_while_holding_ctrl_alt_cancels_the_tap()
    {
        var detector = new ReturnKeyDetector();
        detector.Feed(LeftControl, true);
        detector.Feed(LeftAlt, true);
        detector.Spoil();
        detector.Feed(LeftAlt, false);
        Assert.False(detector.Feed(LeftControl, false));

        // The next tap starts afresh.
        Assert.True(Tap(detector, (LeftControl, true), (LeftAlt, true), (LeftAlt, false), (LeftControl, false)));
    }

    [Fact]
    public void Alt_gr_is_typing_not_a_tap()
    {
        var detector = new ReturnKeyDetector();
        Assert.False(detector.Feed(LeftControl, true, ReturnKeyDetector.AltGrControlScanCode));
        Assert.False(detector.Feed(RightAlt, true));
        Assert.False(detector.Feed(LeftControl, false, ReturnKeyDetector.AltGrControlScanCode));
        Assert.False(detector.Feed(RightAlt, false));
    }

    [Fact]
    public void Releases_of_keys_held_before_the_detector_saw_them_are_ignored()
    {
        // Ctrl+Alt+Q started capture: Ctrl and Alt come up after the hook went in.
        var detector = new ReturnKeyDetector();
        Assert.False(detector.Feed(LeftAlt, false));
        Assert.False(detector.Feed(LeftControl, false));
        Assert.True(Tap(detector, (LeftControl, true), (LeftAlt, true), (LeftAlt, false), (LeftControl, false)));
    }

    [Fact]
    public void The_tap_can_be_switched_off()
    {
        var detector = new ReturnKeyDetector { CtrlAltTap = false };
        Assert.False(Tap(detector, (LeftControl, true), (LeftAlt, true), (LeftAlt, false), (LeftControl, false)));
    }

    // The Copilot key

    [Fact]
    public void The_copilot_key_returns_when_its_keys_come_up()
    {
        var detector = new ReturnKeyDetector();
        Assert.False(detector.Feed(LeftWin, true));
        Assert.False(detector.Feed(LeftShift, true));
        Assert.True(detector.IsCopilotPress(F23, true));
        Assert.False(detector.Feed(F23, true));
        Assert.False(detector.Feed(F23, false));
        Assert.False(detector.Feed(LeftShift, false));
        Assert.True(detector.Feed(LeftWin, false));
    }

    [Fact]
    public void Win_shift_without_f23_is_an_ordinary_shortcut()
    {
        var detector = new ReturnKeyDetector();
        Assert.False(Tap(detector, (LeftWin, true), (LeftShift, true), (0x53, true), (0x53, false),
            (LeftShift, false), (LeftWin, false)));
        Assert.False(Tap(detector, (LeftWin, true), (LeftShift, true), (LeftShift, false), (LeftWin, false)));
    }

    [Fact]
    public void F23_without_win_is_not_the_copilot_key()
    {
        var detector = new ReturnKeyDetector();
        Assert.False(detector.IsCopilotPress(F23, true));
        Assert.False(Tap(detector, (F23, true), (F23, false)));
    }

    [Fact]
    public void The_copilot_key_can_be_switched_off()
    {
        var detector = new ReturnKeyDetector { CopilotKey = false };
        Assert.False(detector.IsCopilotPress(F23, true));
        Assert.False(Tap(detector, (LeftWin, true), (LeftShift, true), (F23, true), (F23, false),
            (LeftShift, false), (LeftWin, false)));
    }

    // In the capture hook

    private static (InputCapture Capture, List<string> Events) Capturing(bool tap = true, bool copilot = true)
    {
        var capture = new InputCapture();
        capture.SetReturnKeys(tap, copilot);
        capture.SetPassThrough(false);
        var events = new List<string>();
        capture.StopRequested += () => events.Add("stop");
        capture.KeyboardReport += (modifiers, usages) =>
            events.Add(modifiers == KeyModifiers.None && usages.Length == 0 ? "release" : "keys");
        return (capture, events);
    }

    [Fact]
    public void A_ctrl_alt_tap_releases_the_phone_s_keys_then_stops_and_swallows_every_key()
    {
        var (capture, events) = Capturing();
        using var _ = capture;

        Assert.True(capture.HandleKeyboardEvent(LeftControl, true));
        Assert.True(capture.HandleKeyboardEvent(LeftAlt, true));
        Assert.True(capture.HandleKeyboardEvent(LeftAlt, false));
        Assert.DoesNotContain("stop", events);
        Assert.True(capture.HandleKeyboardEvent(LeftControl, false));

        Assert.Equal("release", events[^2]);
        Assert.Equal("stop", events[^1]);
    }

    [Fact]
    public void Ctrl_alt_tap_does_nothing_while_input_stays_on_this_pc()
    {
        using var capture = new InputCapture();
        capture.SetReturnKeys(true, true);
        capture.SetPassThrough(true);
        var stops = 0;
        capture.StopRequested += () => stops++;

        Assert.False(capture.HandleKeyboardEvent(LeftControl, true));
        Assert.False(capture.HandleKeyboardEvent(LeftAlt, true));
        Assert.False(capture.HandleKeyboardEvent(LeftAlt, false));
        Assert.False(capture.HandleKeyboardEvent(LeftControl, false));
        Assert.Equal(0, stops);
    }

    [Fact]
    public void Ctrl_alt_with_another_key_still_reaches_the_phone()
    {
        var (capture, events) = Capturing();
        using var _ = capture;

        capture.HandleKeyboardEvent(LeftControl, true);
        capture.HandleKeyboardEvent(LeftAlt, true);
        Assert.True(capture.HandleKeyboardEvent(0x54, true)); // T
        capture.HandleKeyboardEvent(0x54, false);
        capture.HandleKeyboardEvent(LeftAlt, false);
        capture.HandleKeyboardEvent(LeftControl, false);

        Assert.DoesNotContain("stop", events);
    }

    [Fact]
    public void A_mouse_click_during_ctrl_alt_keeps_input_on_the_phone()
    {
        var (capture, events) = Capturing();
        using var _ = capture;

        capture.HandleKeyboardEvent(LeftControl, true);
        capture.HandleKeyboardEvent(LeftAlt, true);
        capture.HandleMouseButtonEvent(MouseButtons.Left, true);
        capture.HandleMouseButtonEvent(MouseButtons.Left, false);
        capture.HandleKeyboardEvent(LeftAlt, false);
        capture.HandleKeyboardEvent(LeftControl, false);

        Assert.DoesNotContain("stop", events);
    }

    [Fact]
    public void Without_the_setting_ctrl_alt_is_just_ctrl_and_alt()
    {
        var (capture, events) = Capturing(tap: false);
        using var _ = capture;

        capture.HandleKeyboardEvent(LeftControl, true);
        capture.HandleKeyboardEvent(LeftAlt, true);
        capture.HandleKeyboardEvent(LeftAlt, false);
        capture.HandleKeyboardEvent(LeftControl, false);

        Assert.DoesNotContain("stop", events);
    }

    [Fact]
    public void The_copilot_key_stops_without_sending_f23_to_the_phone()
    {
        var (capture, events) = Capturing();
        using var _ = capture;
        var usagesSent = new List<byte>();
        capture.KeyboardReport += (_, usages) => usagesSent.AddRange(usages);

        Assert.True(capture.HandleKeyboardEvent(LeftWin, true));
        Assert.True(capture.HandleKeyboardEvent(LeftShift, true));
        Assert.True(capture.HandleKeyboardEvent(F23, true));
        Assert.True(capture.HandleKeyboardEvent(F23, false));
        Assert.True(capture.HandleKeyboardEvent(LeftShift, false));
        Assert.DoesNotContain("stop", events);
        Assert.True(capture.HandleKeyboardEvent(LeftWin, false));

        Assert.Equal("stop", events[^1]);
        Assert.Empty(usagesSent);
    }

    [Fact]
    public void A_changed_stop_hotkey_replaces_ctrl_alt_q()
    {
        using var capture = new InputCapture();
        capture.SetStopHotkey(Hotkey.Parse("Ctrl+Shift+F9"));
        capture.SetPassThrough(false);
        var stops = 0;
        capture.StopRequested += () => stops++;

        capture.HandleKeyboardEvent(LeftControl, true);
        capture.HandleKeyboardEvent(LeftAlt, true);
        capture.HandleKeyboardEvent(0x51, true);
        Assert.Equal(0, stops);
        capture.HandleKeyboardEvent(0x51, false);
        capture.HandleKeyboardEvent(LeftAlt, false);

        capture.HandleKeyboardEvent(LeftShift, true);
        Assert.True(capture.HandleKeyboardEvent(0x78, true));
        Assert.Equal(1, stops);
    }

    // Touching outside the picture

    private static readonly VideoRect Picture = new(100, 100, 800, 450);
    private static bool OnPicture(double x, double y) => Picture.Contains(x, y);
    private static bool OnBar(double x, double y) => y is >= 600 and <= 660 && x is >= 300 and <= 700;

    [Fact]
    public void A_finger_landing_outside_the_picture_returns()
    {
        var detector = new OutsideTouchDetector();
        Assert.True(detector.Update([new RawContact(1, true, 20, 20)], OnPicture, OnBar));
    }

    [Fact]
    public void Fingers_on_the_picture_or_the_bar_stay_with_the_phone()
    {
        var detector = new OutsideTouchDetector();
        Assert.False(detector.Update([new RawContact(1, true, 500, 300)], OnPicture, OnBar));
        Assert.False(detector.Update([new RawContact(2, true, 500, 630)], OnPicture, OnBar));
    }

    [Fact]
    public void A_finger_sliding_off_the_picture_is_still_the_phone_s()
    {
        var detector = new OutsideTouchDetector();
        Assert.False(detector.Update([new RawContact(1, true, 500, 300)], OnPicture, OnBar));
        Assert.False(detector.Update([new RawContact(1, true, 20, 20)], OnPicture, OnBar));
    }

    [Fact]
    public void A_lifted_finger_landing_again_outside_counts()
    {
        var detector = new OutsideTouchDetector();
        detector.Update([new RawContact(1, true, 500, 300)], OnPicture, OnBar);
        detector.Update([new RawContact(1, false, 500, 300)], OnPicture, OnBar);
        Assert.True(detector.Update([new RawContact(1, true, 20, 20)], OnPicture, OnBar));
    }

    [Fact]
    public void A_second_finger_landing_outside_counts()
    {
        var detector = new OutsideTouchDetector();
        detector.Update([new RawContact(1, true, 500, 300)], OnPicture, OnBar);
        Assert.True(detector.Update([new RawContact(1, true, 500, 300), new RawContact(2, true, 950, 50)], OnPicture, OnBar));
    }

    // Tray colour

    [Fact]
    public void The_live_tray_icon_turns_blue_red_and_leaves_white_and_orange()
    {
        var (r, g, b) = LiveTint.Apply(0x1E, 0x88, 0xFF);
        Assert.True(r > 200 && g < 80 && b < 60);
        Assert.Equal(((byte)255, (byte)255, (byte)255), LiveTint.Apply(255, 255, 255));
        Assert.Equal(((byte)0xF5, (byte)0xB0, (byte)0x20), LiveTint.Apply(0xF5, 0xB0, 0x20));
    }
}
