using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

/// <summary>
/// Caps Lock is lock state the host owns and never reports back, so it is inferred from the
/// presses we forward. These cover the edge detection that inference depends on.
/// </summary>
public sealed class CapsLockTrackingTests
{
    private const byte CapsLockUsage = 0x39;
    private const byte AUsage = 0x04;

    [Fact]
    public void A_report_carrying_caps_lock_is_recognised()
    {
        var report = HidReports.Keyboard(KeyModifiers.None, CapsLockUsage);

        Assert.True(BleHidPeripheral.LockKeysIn(report).HasFlag(LockKeys.CapsLock));
    }

    [Fact]
    public void A_report_without_caps_lock_is_not_recognised()
    {
        var report = HidReports.Keyboard(KeyModifiers.None, AUsage);

        Assert.False(BleHidPeripheral.LockKeysIn(report).HasFlag(LockKeys.CapsLock));
    }

    [Fact]
    public void An_empty_release_report_does_not_count_as_caps_lock()
    {
        Assert.False(BleHidPeripheral.LockKeysIn(HidReports.KeyboardRelease()).HasFlag(LockKeys.CapsLock));
    }

    [Fact]
    public void Caps_lock_is_found_in_any_of_the_six_key_slots()
    {
        var report = HidReports.Keyboard(KeyModifiers.None, AUsage, 0x05, 0x06, 0x07, 0x08, CapsLockUsage);

        Assert.True(BleHidPeripheral.LockKeysIn(report).HasFlag(LockKeys.CapsLock));
    }

    [Fact]
    public void The_modifier_byte_is_never_mistaken_for_a_caps_lock_usage()
    {
        // 0x39 as a modifier bit pattern must not look like the Caps Lock usage, which lives
        // only in the key slots.
        var report = HidReports.Keyboard((KeyModifiers)CapsLockUsage);

        Assert.False(BleHidPeripheral.LockKeysIn(report).HasFlag(LockKeys.CapsLock));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_host_switch_hotkey_never_forwards_caps_lock(bool passThrough)
    {
        // Ctrl+Alt+CapsLock switches target. It is swallowed rather than sent, so it must not
        // move the host's lock state - and therefore must not reach a keyboard report at all.
        using var capture = new InputCapture();
        capture.SetPassThrough(passThrough);

        var forwarded = new List<byte[]>();
        capture.KeyboardReport += (_, usages) => forwarded.Add(usages);

        capture.HandleKeyboardEvent(0xA2, true); // Left Control
        capture.HandleKeyboardEvent(0xA4, true); // Left Alt
        capture.HandleKeyboardEvent(0x14, true); // Caps Lock
        capture.HandleKeyboardEvent(0x14, false);

        Assert.DoesNotContain(forwarded, usages => usages.Contains(CapsLockUsage));
    }

    [Fact]
    public void A_plain_caps_lock_press_is_forwarded_while_capturing()
    {
        // Without the modifiers it is an ordinary key, and the host is meant to toggle on it.
        using var capture = new InputCapture();
        capture.SetPassThrough(false);

        var forwarded = new List<byte[]>();
        capture.KeyboardReport += (_, usages) => forwarded.Add(usages);

        capture.HandleKeyboardEvent(0x14, true);

        Assert.Contains(forwarded, usages => usages.Contains(CapsLockUsage));
    }

    [Theory]
    [InlineData(0x00, false)] // nothing lit
    [InlineData(0x02, true)]  // caps lock only
    [InlineData(0x01, false)] // num lock only
    [InlineData(0x04, false)] // scroll lock only
    [InlineData(0x03, true)]  // num lock + caps lock
    [InlineData(0x07, true)]  // num + caps + scroll
    [InlineData(0x1F, true)]  // every lamp
    public void Caps_lock_is_bit_one_of_the_led_report(byte leds, bool expected)
    {
        Assert.Equal(expected, (leds & HidDescriptors.LedCapsLock) != 0);
    }

    [Fact]
    public void The_report_map_declares_an_led_output_report()
    {
        var map = HidDescriptors.ReportMapValue;

        // 0x91 is an Output item; without one the host has no channel to report lock state.
        Assert.Contains(0x91, map.Select(b => (int)b));
    }

    [Fact]
    public void The_led_output_report_is_declared_inside_the_keyboard_collection()
    {
        var map = HidDescriptors.ReportMapValue;
        var output = Array.IndexOf(map, (byte)0x91);
        var mouseUsage = IndexOfSequence(map, [0x09, 0x02]);

        Assert.True(output > 0, "no Output item found");
        Assert.True(output < mouseUsage, "the LED report must belong to the keyboard, not the mouse");
    }

    [Fact]
    public void The_led_output_report_covers_five_lamps_and_pads_to_a_byte()
    {
        var map = HidDescriptors.ReportMapValue;

        // Five 1-bit fields plus three bits of padding; a host that reads this expects one byte.
        Assert.True(IndexOfSequence(map, [0x95, 0x05, 0x75, 0x01]) > 0, "expected five 1-bit LED fields");
        Assert.True(IndexOfSequence(map, [0x95, 0x01, 0x75, 0x03, 0x91, 0x01]) > 0, "expected 3 bits of padding");
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++)
                if (haystack[i + j] != needle[j]) match = false;
            if (match) return i;
        }
        return -1;
    }

    [Fact]
    public void Num_lock_is_recognised_in_a_report()
    {
        var report = HidReports.Keyboard(KeyModifiers.None, 0x53);

        Assert.True(BleHidPeripheral.LockKeysIn(report).HasFlag(LockKeys.NumLock));
        Assert.False(BleHidPeripheral.LockKeysIn(report).HasFlag(LockKeys.CapsLock));
    }

    [Fact]
    public void Both_lock_keys_can_be_present_at_once()
    {
        var report = HidReports.Keyboard(KeyModifiers.None, CapsLockUsage, 0x53);

        Assert.Equal(LockKeys.CapsLock | LockKeys.NumLock, BleHidPeripheral.LockKeysIn(report));
    }

    [Theory]
    [InlineData(0x00, LockKeys.None)]
    [InlineData(0x01, LockKeys.NumLock)]
    [InlineData(0x02, LockKeys.CapsLock)]
    [InlineData(0x03, LockKeys.NumLock | LockKeys.CapsLock)]
    [InlineData(0x04, LockKeys.None)]                      // scroll lock is ignored
    [InlineData(0x1F, LockKeys.NumLock | LockKeys.CapsLock)] // every lamp, only two tracked
    public void An_led_report_maps_straight_onto_the_tracked_lock_keys(byte leds, LockKeys expected)
    {
        Assert.Equal(expected, (LockKeys)(leds & HidDescriptors.LedTrackedMask));
    }

    [Theory]
    // Only the states that can silently corrupt input are reported.
    [InlineData(LockKeys.NumLock, true, null)]
    [InlineData(LockKeys.NumLock | LockKeys.CapsLock, true, "CAPS LOCK ON")]
    [InlineData(LockKeys.None, true, "NUM LOCK OFF")]
    [InlineData(LockKeys.CapsLock, true, "CAPS LOCK ON, NUM LOCK OFF")]
    // Num lock off is silent until the numpad is actually used: hosts without a Num Lock key
    // report it permanently off, and an unprompted warning there would never clear.
    [InlineData(LockKeys.None, false, null)]
    [InlineData(LockKeys.CapsLock, false, "CAPS LOCK ON")]
    public void Only_lock_states_that_can_bite_are_described(LockKeys keys, bool numpadUsed, string? expected)
    {
        Assert.Equal(expected, BleHidPeripheral.DescribeLockKeys(keys, numpadUsed));
        Assert.Equal(expected, BleHidPeripheral.DescribeTargetLockKeys([(keys, numpadUsed)]));
    }

    [Theory]
    [InlineData(LockKeys.NumLock, true, LockKeys.None, true, "NUM LOCK OFF")]
    [InlineData(LockKeys.None, true, LockKeys.NumLock, true, "NUM LOCK OFF")]
    [InlineData(LockKeys.NumLock, true, LockKeys.None, false, null)]
    [InlineData(LockKeys.CapsLock | LockKeys.NumLock, false, LockKeys.None, true, "CAPS LOCK ON, NUM LOCK OFF")]
    [InlineData(LockKeys.CapsLock, false, LockKeys.NumLock, true, "CAPS LOCK ON")]
    [InlineData(LockKeys.NumLock, true, LockKeys.NumLock, true, null)]
    [InlineData(LockKeys.None, false, LockKeys.None, false, null)]
    public void Broadcast_lock_warnings_are_evaluated_per_host(
        LockKeys firstKeys, bool firstNumpadUsed, LockKeys secondKeys, bool secondNumpadUsed, string? expected)
    {
        Assert.Equal(expected, BleHidPeripheral.DescribeTargetLockKeys(
            [(firstKeys, firstNumpadUsed), (secondKeys, secondNumpadUsed)]));
    }

    [Fact]
    public void No_targeted_hosts_produces_no_lock_warning()
    {
        Assert.Null(BleHidPeripheral.DescribeTargetLockKeys([]));
    }

    [Theory]
    [InlineData(0x90, 0x53)] // Num Lock
    [InlineData(0x60, 0x62)] // Numpad 0
    [InlineData(0x61, 0x59)] // Numpad 1
    [InlineData(0x69, 0x61)] // Numpad 9
    [InlineData(0x6F, 0x54)] // Numpad /
    [InlineData(0x6A, 0x55)] // Numpad *
    [InlineData(0x6D, 0x56)] // Numpad -
    [InlineData(0x6B, 0x57)] // Numpad +
    [InlineData(0x6E, 0x63)] // Numpad .
    public void The_numpad_and_num_lock_map_to_their_hid_usages(int virtualKey, byte expected)
    {
        Assert.True(VirtualKeyMap.TryGetUsage(virtualKey, out var usage));
        Assert.Equal(expected, usage);
    }

    [Fact]
    public void Numpad_digits_do_not_collide_with_the_letter_row()
    {
        // VK_NUMPAD1..9 sit just below VK_A, so an off-by-one here would silently type letters.
        VirtualKeyMap.TryGetUsage(0x61, out var numpadOne);
        VirtualKeyMap.TryGetUsage(0x31, out var rowOne);

        Assert.NotEqual(rowOne, numpadOne);
    }

    [Theory]
    // Non-extended scan codes are the numpad; the same codes with the extended flag are the
    // navigation cluster. Resolution must not depend on the local Num Lock state.
    [InlineData(0x4F, false, 0x59)] // Keypad 1
    [InlineData(0x50, false, 0x5A)] // Keypad 2
    [InlineData(0x51, false, 0x5B)] // Keypad 3
    [InlineData(0x4B, false, 0x5C)] // Keypad 4
    [InlineData(0x4C, false, 0x5D)] // Keypad 5
    [InlineData(0x4D, false, 0x5E)] // Keypad 6
    [InlineData(0x47, false, 0x5F)] // Keypad 7
    [InlineData(0x48, false, 0x60)] // Keypad 8
    [InlineData(0x49, false, 0x61)] // Keypad 9
    [InlineData(0x52, false, 0x62)] // Keypad 0
    [InlineData(0x53, false, 0x63)] // Keypad .
    public void Numpad_keys_resolve_from_the_physical_key(uint scanCode, bool extended, byte expected)
    {
        // Num Lock on: Windows reports VK_NUMPAD*.
        Assert.True(VirtualKeyMap.TryGetUsage(0x60, scanCode, extended, out var withNumLock));
        Assert.Equal(expected, withNumLock);

        // Num Lock off: Windows reports the navigation key instead. Same physical key, so the
        // host must still receive the keypad usage and apply its own Num Lock.
        Assert.True(VirtualKeyMap.TryGetUsage(0x23, scanCode, extended, out var withoutNumLock));
        Assert.Equal(expected, withoutNumLock);
    }

    [Theory]
    [InlineData(0x47, 0x24, 0x4A)] // Home
    [InlineData(0x4F, 0x23, 0x4D)] // End
    [InlineData(0x48, 0x26, 0x52)] // Up
    [InlineData(0x50, 0x28, 0x51)] // Down
    [InlineData(0x52, 0x2D, 0x49)] // Insert
    [InlineData(0x53, 0x2E, 0x4C)] // Delete
    public void The_navigation_cluster_is_not_mistaken_for_the_numpad(uint scanCode, int virtualKey, byte expected)
    {
        // Same scan codes as the numpad, but extended - these are the dedicated nav keys.
        Assert.True(VirtualKeyMap.TryGetUsage(virtualKey, scanCode, extended: true, out var usage));
        Assert.Equal(expected, usage);
    }

    [Fact]
    public void Keypad_enter_is_distinguished_from_the_main_enter()
    {
        Assert.True(VirtualKeyMap.TryGetUsage(0x0D, 0x1C, extended: false, out var mainEnter));
        Assert.Equal(0x28, mainEnter);

        Assert.True(VirtualKeyMap.TryGetUsage(0x0D, 0x1C, extended: true, out var keypadEnter));
        Assert.Equal(0x58, keypadEnter);
    }

    [Fact]
    public void Keys_without_a_scan_code_still_resolve_by_virtual_key()
    {
        // Injected input can arrive with no scan code; the virtual-key map remains the fallback.
        Assert.True(VirtualKeyMap.TryGetUsage(0x41, 0, extended: false, out var a));
        Assert.Equal(0x04, a);
    }

    [Fact]
    public void The_led_output_fields_declare_a_one_bit_logical_range()
    {
        // Logical Minimum/Maximum are global items that persist until changed, so the 0..101
        // range declared for the key array would otherwise still be in force for the LED bits.
        var map = HidDescriptors.ReportMapValue;
        int logicalMax = -1;

        for (var i = 0; i + 1 < map.Length; i += 2)
        {
            if (map[i] == 0x25) logicalMax = map[i + 1];       // Logical Maximum
            if (map[i] == 0x91 && map[i + 1] == 0x02)          // Output (Data,Var,Abs)
            {
                Assert.Equal(1, logicalMax);
                return;
            }
        }

        Assert.Fail("no LED Output item found in the report map");
    }
}
