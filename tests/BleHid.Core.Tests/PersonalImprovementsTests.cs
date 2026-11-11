using BleHid.Core;
using Xunit;

namespace BleHid.Core.Tests;

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
