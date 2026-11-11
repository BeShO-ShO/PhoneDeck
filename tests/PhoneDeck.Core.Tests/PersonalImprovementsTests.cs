using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public sealed class PersonalImprovementsTests
{
    private const int LeftControl = 0xA2;
    private const int LeftAlt = 0xA4;
    private const int Q = 0x51;
    private static readonly string[] Hosts = ["phone", "tablet"];

    [Fact]
    public void Restore_finds_the_saved_host_ignoring_case()
    {
        Assert.Equal(1, TargetRestore.FindIndex(false, false, true, "TABLET", Hosts));
    }

    [Fact]
    public void Restore_never_moves_input_while_capturing()
    {
        Assert.Equal(-1, TargetRestore.FindIndex(false, capturing: true, true, "phone", Hosts));
    }

    [Fact]
    public void Restore_respects_a_choice_already_made()
    {
        Assert.Equal(-1, TargetRestore.FindIndex(alreadyDecided: true, false, true, "phone", Hosts));
    }

    [Fact]
    public void Restore_leaves_an_existing_host_target_alone()
    {
        Assert.Equal(-1, TargetRestore.FindIndex(false, false, isLocalTarget: false, "phone", Hosts));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("gone")]
    public void Restore_does_nothing_without_a_connected_saved_host(string? saved)
    {
        Assert.Equal(-1, TargetRestore.FindIndex(false, false, true, saved, Hosts));
    }

    [Fact]
    public void Stop_hotkey_releases_a_held_mouse_button_before_stopping()
    {
        using var capture = new InputCapture();
        var events = new List<string>();
        capture.MouseReport += (buttons, _, _, _) => events.Add($"mouse:{buttons}");
        capture.StopRequested += () => events.Add("stop");

        capture.HandleMouseButtonEvent(MouseButtons.Left, true);
        capture.HandleKeyboardEvent(LeftControl, true);
        capture.HandleKeyboardEvent(LeftAlt, true);
        Assert.True(capture.HandleKeyboardEvent(Q, true));

        Assert.Equal(["mouse:Left", "mouse:None", "stop"], events);
    }

    [Theory]
    [InlineData(false, false, true)]  // redirecting needs pointer deltas
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]    // local, but the shake gesture watches motion
    [InlineData(true, false, false)]  // local with no gesture: nothing to hook for
    public void Mouse_hook_is_only_needed_when_something_uses_it(bool passThrough, bool gesture, bool expected)
    {
        Assert.Equal(expected, InputCapture.NeedsMouseHook(passThrough, gesture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disabled_chord_does_not_switch_target(bool passThrough)
    {
        using var capture = new InputCapture();
        capture.SetPassThrough(passThrough);
        capture.SetChordSwitch(false);
        var switches = 0;
        capture.SwitchHostRequested += () => switches++;

        capture.HandleKeyboardEvent(LeftControl, true);
        capture.HandleKeyboardEvent(0x44, true);
        capture.HandleKeyboardEvent(0x43, true);

        Assert.Equal(0, switches);
    }

    [Fact]
    public void Disabled_chord_leaves_the_keys_working_locally()
    {
        using var capture = new InputCapture();
        capture.SetPassThrough(true);
        capture.SetChordSwitch(false);

        capture.HandleKeyboardEvent(LeftControl, true);
        capture.HandleKeyboardEvent(0x44, true);

        Assert.False(capture.HandleKeyboardEvent(0x43, true));
    }

    [Fact]
    public void Disabled_chord_keeps_the_caps_lock_switch()
    {
        using var capture = new InputCapture();
        capture.SetChordSwitch(false);
        var switches = 0;
        capture.SwitchHostRequested += () => switches++;

        capture.HandleKeyboardEvent(LeftControl, true);
        capture.HandleKeyboardEvent(LeftAlt, true);
        capture.HandleKeyboardEvent(0x14, true);

        Assert.Equal(1, switches);
    }

    [Fact]
    public void Disabled_caps_lock_switch_sends_caps_lock_instead_of_switching()
    {
        using var capture = new InputCapture();
        capture.SetCapsLockSwitch(false);
        var switches = 0;
        var reports = new List<byte[]>();
        capture.SwitchHostRequested += () => switches++;
        capture.KeyboardReport += (_, usages) => reports.Add(usages);

        capture.HandleKeyboardEvent(LeftControl, true);
        capture.HandleKeyboardEvent(LeftAlt, true);
        capture.HandleKeyboardEvent(0x14, true);

        Assert.Equal(0, switches);
        Assert.Contains(reports, usages => usages.Contains((byte)0x39));
    }

    [Fact]
    public void Disabled_caps_lock_switch_keeps_the_stop_hotkey()
    {
        using var capture = new InputCapture();
        capture.SetCapsLockSwitch(false);
        var stops = 0;
        capture.StopRequested += () => stops++;

        capture.HandleKeyboardEvent(LeftControl, true);
        capture.HandleKeyboardEvent(LeftAlt, true);
        capture.HandleKeyboardEvent(Q, true);

        Assert.Equal(1, stops);
    }

    [Fact]
    public void Start_moves_off_this_pc_to_the_remembered_phone()
    {
        Assert.Equal(1, TargetRestore.StartIndex(true, "TABLET", Hosts));
    }

    [Fact]
    public void Start_falls_back_to_the_first_device_when_the_remembered_one_is_missing()
    {
        Assert.Equal(0, TargetRestore.StartIndex(true, "gone", Hosts));
        Assert.Equal(0, TargetRestore.StartIndex(true, null, Hosts));
    }

    [Fact]
    public void Start_keeps_a_device_that_is_already_selected()
    {
        Assert.Equal(-1, TargetRestore.StartIndex(false, "phone", Hosts));
    }

    [Fact]
    public void Start_stays_on_this_pc_when_nothing_is_connected()
    {
        Assert.Equal(-1, TargetRestore.StartIndex(true, "phone", []));
    }

    [Fact]
    public void Summary_says_not_running()
    {
        Assert.Equal("Not running", ConnectionSummary.Describe(false, ["phone"]));
    }

    [Fact]
    public void Summary_says_when_nothing_is_connected()
    {
        Assert.Equal("No device connected", ConnectionSummary.Describe(true, []));
    }

    [Fact]
    public void Summary_names_a_single_device()
    {
        Assert.Equal("Connected to phone", ConnectionSummary.Describe(true, ["phone"]));
    }

    [Fact]
    public void Summary_counts_several_devices()
    {
        Assert.Equal("Connected to 2 devices", ConnectionSummary.Describe(true, ["phone", "tablet"]));
    }

    [Fact]
    public async Task Shutdown_step_reports_success()
    {
        Assert.True(await CaptureSession.CompletesWithinAsync(
            () => Task.CompletedTask, TimeSpan.FromSeconds(1), _ => { }, "step"));
    }

    [Fact]
    public async Task Shutdown_step_gives_up_on_a_hung_send()
    {
        var logged = new List<string>();
        var hung = new TaskCompletionSource();

        var finished = await CaptureSession.CompletesWithinAsync(
            () => hung.Task, TimeSpan.FromMilliseconds(50), logged.Add, "key release");

        Assert.False(finished);
        Assert.Contains(logged, line => line.Contains("key release timed out"));
    }

    [Fact]
    public async Task Shutdown_step_contains_a_failure_so_later_steps_still_run()
    {
        var logged = new List<string>();

        var finished = await CaptureSession.CompletesWithinAsync(
            () => throw new InvalidOperationException("radio gone"),
            TimeSpan.FromSeconds(1), logged.Add, "key release");

        Assert.False(finished);
        Assert.Contains(logged, line => line.Contains("radio gone"));
    }

    [Fact]
    public async Task Shutdown_step_treats_cancellation_as_a_clean_stop()
    {
        Assert.True(await CaptureSession.CompletesWithinAsync(
            () => Task.FromCanceled(new CancellationToken(true)),
            TimeSpan.FromSeconds(1), _ => { }, "report pump"));
    }
}
