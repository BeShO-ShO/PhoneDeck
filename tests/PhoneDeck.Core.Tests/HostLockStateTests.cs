using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

/// <summary>
/// The rules that decide whether a host's lock state is believed, measured, or forgotten.
/// These used to be reachable only through a live GATT stack, so none of them were covered.
/// </summary>
public sealed class HostLockStateTests
{
    private const string HostA = "BluetoothLE#BluetoothLE00:00:00:00:00:01-aa:aa:aa:aa:aa:aa";
    private const string HostB = "BluetoothLE#BluetoothLE00:00:00:00:00:01-bb:bb:bb:bb:bb:bb";

    private const byte LedNone = 0x00;
    private const byte LedNumLock = 0x01;
    private const byte LedCapsLock = 0x02;

    [Fact]
    public void A_new_host_starts_clear_and_unmeasured()
    {
        var state = new HostLockState();

        Assert.Equal(LockKeys.None, state.For(HostA));
        Assert.False(state.Measured(HostA));
        Assert.False(state.NumpadUsed(HostA));
    }

    [Fact]
    public void Forwarded_lock_keys_toggle_the_inferred_state()
    {
        var state = new HostLockState();

        state.NoteForwarded(HostA, LockKeys.CapsLock);
        Assert.Equal(LockKeys.CapsLock, state.For(HostA));

        state.NoteForwarded(HostA, LockKeys.CapsLock);
        Assert.Equal(LockKeys.None, state.For(HostA));
    }

    [Fact]
    public void Inference_leaves_the_other_lock_key_alone()
    {
        var state = new HostLockState();

        state.NoteForwarded(HostA, LockKeys.CapsLock);
        state.NoteForwarded(HostA, LockKeys.NumLock);

        Assert.Equal(LockKeys.CapsLock | LockKeys.NumLock, state.For(HostA));
    }

    [Fact]
    public void An_led_report_replaces_whatever_was_inferred()
    {
        var state = new HostLockState();
        state.NoteForwarded(HostA, LockKeys.CapsLock);
        Assert.Equal(LockKeys.CapsLock, state.For(HostA));

        // The host says caps is off, so the guess was wrong and must be discarded.
        state.NoteLedReport(HostA, LedNone);

        Assert.Equal(LockKeys.None, state.For(HostA));
        Assert.True(state.Measured(HostA));
    }

    [Fact]
    public void Inference_stops_once_a_host_reports_its_lamps()
    {
        var state = new HostLockState();
        state.NoteLedReport(HostA, LedNone);

        // Guessing alongside a known good signal would flicker the display, and would be wrong
        // whenever the host declined to act on the key.
        state.NoteForwarded(HostA, LockKeys.CapsLock);

        Assert.Equal(LockKeys.None, state.For(HostA));
    }

    [Fact]
    public void The_first_led_report_is_announced_once()
    {
        var state = new HostLockState();

        var first = state.NoteLedReport(HostA, LedCapsLock);
        var second = state.NoteLedReport(HostA, LedCapsLock);

        Assert.True(first.FirstReport);
        Assert.False(second.FirstReport);
    }

    [Fact]
    public void Only_a_real_change_is_reported_as_changed()
    {
        var state = new HostLockState();
        state.NoteLedReport(HostA, LedCapsLock);

        Assert.False(state.NoteLedReport(HostA, LedCapsLock).Changed);
        Assert.True(state.NoteLedReport(HostA, LedNone).Changed);
    }

    [Fact]
    public void Scroll_lock_and_unknown_lamps_are_ignored()
    {
        var state = new HostLockState();

        var result = state.NoteLedReport(HostA, 0xFF);

        Assert.Equal(LockKeys.NumLock | LockKeys.CapsLock, result.State);
    }

    [Fact]
    public void Hosts_never_see_each_others_lock_state()
    {
        var state = new HostLockState();

        state.NoteLedReport(HostA, LedCapsLock);
        state.NoteLedReport(HostB, LedNumLock);

        Assert.Equal(LockKeys.CapsLock, state.For(HostA));
        Assert.Equal(LockKeys.NumLock, state.For(HostB));
    }

    [Fact]
    public void One_host_being_measured_does_not_measure_another()
    {
        var state = new HostLockState();
        state.NoteLedReport(HostA, LedNone);

        state.NoteForwarded(HostB, LockKeys.CapsLock);

        Assert.True(state.Measured(HostA));
        Assert.False(state.Measured(HostB));
        // B is still inferred, so its guess must still be applied.
        Assert.Equal(LockKeys.CapsLock, state.For(HostB));
    }

    [Fact]
    public void Numpad_use_is_tracked_per_host()
    {
        var state = new HostLockState();

        state.NoteNumpadUsed(HostA);

        Assert.True(state.NumpadUsed(HostA));
        Assert.False(state.NumpadUsed(HostB));
    }

    [Fact]
    public void Disconnecting_forgets_everything_about_that_host()
    {
        var state = new HostLockState();
        state.NoteLedReport(HostA, LedCapsLock);
        state.NoteNumpadUsed(HostA);

        // The host owns its lock state and can change it while we are not attached, so none of
        // this survives the disconnect.
        state.Forget(HostA);

        Assert.Equal(LockKeys.None, state.For(HostA));
        Assert.False(state.Measured(HostA));
        Assert.False(state.NumpadUsed(HostA));
    }

    [Fact]
    public void Forgetting_one_host_leaves_the_other_intact()
    {
        var state = new HostLockState();
        state.NoteLedReport(HostA, LedCapsLock);
        state.NoteLedReport(HostB, LedCapsLock);

        state.Forget(HostA);

        Assert.Equal(LockKeys.None, state.For(HostA));
        Assert.Equal(LockKeys.CapsLock, state.For(HostB));
    }

    [Fact]
    public void A_reconnected_host_can_be_measured_again()
    {
        var state = new HostLockState();
        state.NoteLedReport(HostA, LedCapsLock);
        state.Forget(HostA);

        var result = state.NoteLedReport(HostA, LedCapsLock);

        Assert.True(result.FirstReport);
        Assert.True(state.Measured(HostA));
    }

    [Fact]
    public void Device_ids_are_matched_without_regard_to_case()
    {
        var state = new HostLockState();

        state.NoteLedReport(HostA.ToUpperInvariant(), LedCapsLock);

        Assert.Equal(LockKeys.CapsLock, state.For(HostA.ToLowerInvariant()));
    }

    [Fact]
    public void Forwarding_nothing_does_not_create_a_host()
    {
        var state = new HostLockState();

        state.NoteForwarded(HostA, LockKeys.None);

        Assert.Equal(LockKeys.None, state.For(HostA));
        Assert.False(state.Measured(HostA));
    }
}
