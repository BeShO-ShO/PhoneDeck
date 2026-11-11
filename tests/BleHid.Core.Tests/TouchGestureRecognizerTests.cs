using BleHid.Core;
using Xunit;

namespace BleHid.Core.Tests;

public sealed class TouchGestureRecognizerTests
{
    private readonly TouchGestureRecognizer _touch = new();

    private static GestureAction[] Actions(IEnumerable<TouchOutput> outputs) =>
        outputs.Select(o => o.Action).ToArray();

    private void ThreeDown(double y = 500, long now = 0)
    {
        _touch.FingerDown(1, 400, y, now);
        _touch.FingerDown(2, 500, y, now + 10);
        _touch.FingerDown(3, 600, y, now + 20);
    }

    private void MoveAll(double dx, double dy, long now, double y = 500)
    {
        _touch.FingerMove(1, 400 + dx, y + dy, now);
        _touch.FingerMove(2, 500 + dx, y + dy, now);
        _touch.FingerMove(3, 600 + dx, y + dy, now);
    }

    [Fact]
    public void A_quick_tap_sends_a_press_and_release_at_the_finger()
    {
        Assert.Empty(_touch.FingerDown(1, 100, 200, 0));

        var output = _touch.FingerUp(1, 100, 200, 30);

        Assert.Equal([new TouchOutput(GestureAction.Down, 100, 200), new TouchOutput(GestureAction.Up, 100, 200)], output);
    }

    [Fact]
    public void A_finger_held_still_is_pressed_once_the_wait_ends()
    {
        _touch.FingerDown(1, 100, 200, 0);

        Assert.Empty(_touch.Tick(50));
        Assert.Equal([new TouchOutput(GestureAction.Down, 100, 200)], _touch.Tick(80));
        Assert.Empty(_touch.Tick(120));
    }

    [Fact]
    public void Moving_presses_immediately_at_the_original_point()
    {
        _touch.FingerDown(1, 100, 200, 0);

        var output = _touch.FingerMove(1, 100, 240, 10);

        Assert.Equal([new TouchOutput(GestureAction.Down, 100, 200), new TouchOutput(GestureAction.Move, 100, 240)], output);
    }

    [Fact]
    public void Small_jitter_does_not_start_a_drag()
    {
        _touch.FingerDown(1, 100, 200, 0);

        Assert.Empty(_touch.FingerMove(1, 103, 203, 10));
    }

    [Fact]
    public void Two_fingers_become_one_contact_between_them()
    {
        _touch.FingerDown(1, 100, 200, 0);
        _touch.FingerDown(2, 300, 200, 10);

        Assert.Equal([new TouchOutput(GestureAction.Down, 200, 200)], _touch.Tick(100));
    }

    [Fact]
    public void Dragging_two_fingers_moves_the_contact_by_their_average()
    {
        _touch.FingerDown(1, 100, 200, 0);
        _touch.FingerDown(2, 300, 200, 10);
        _touch.Tick(100);

        _touch.FingerMove(1, 100, 260, 110);
        var output = _touch.FingerMove(2, 300, 260, 110);

        Assert.Equal([new TouchOutput(GestureAction.Move, 200, 260)], output);
    }

    [Fact]
    public void Lifting_one_of_two_fingers_does_not_make_the_contact_jump()
    {
        _touch.FingerDown(1, 100, 200, 0);
        _touch.FingerDown(2, 300, 200, 10);
        _touch.Tick(100);
        Assert.Empty(_touch.FingerUp(2, 300, 200, 120));

        var output = _touch.FingerMove(1, 100, 210, 130);

        Assert.Equal([new TouchOutput(GestureAction.Move, 200, 210)], output);
    }

    [Fact]
    public void The_contact_is_released_only_when_the_last_finger_lifts()
    {
        _touch.FingerDown(1, 100, 200, 0);
        _touch.FingerDown(2, 300, 200, 10);
        _touch.Tick(100);

        Assert.Empty(_touch.FingerUp(1, 100, 200, 120));
        Assert.Equal([GestureAction.Up], Actions(_touch.FingerUp(2, 300, 200, 130)));
    }

    [Fact]
    public void Three_fingers_landing_together_send_no_touch()
    {
        ThreeDown();

        Assert.Empty(_touch.Tick(200));
        Assert.Empty(_touch.FingerMove(1, 400, 400, 210));
    }

    [Fact]
    public void Three_finger_swipe_up_goes_home()
    {
        ThreeDown();
        MoveAll(0, -200, 100);

        Assert.Equal([GestureAction.Home], Actions(_touch.FingerUp(1, 400, 300, 120)));
    }

    [Fact]
    public void Three_finger_swipe_up_and_hold_opens_recents()
    {
        ThreeDown();
        MoveAll(0, -200, 100);

        Assert.Equal([GestureAction.Recents], Actions(_touch.FingerUp(1, 400, 300, 600)));
    }

    [Theory]
    [InlineData(-200)]
    [InlineData(200)]
    public void Three_finger_swipe_sideways_goes_back(double dx)
    {
        ThreeDown();
        MoveAll(dx, 0, 100);

        Assert.Equal([GestureAction.Back], Actions(_touch.FingerUp(1, 400 + dx, 500, 120)));
    }

    [Fact]
    public void Three_finger_tap_goes_back()
    {
        ThreeDown();

        Assert.Equal([GestureAction.Back], Actions(_touch.FingerUp(1, 400, 500, 100)));
    }

    [Fact]
    public void Three_fingers_held_still_do_nothing()
    {
        ThreeDown();

        Assert.Empty(_touch.FingerUp(1, 400, 500, 900));
    }

    [Fact]
    public void Three_finger_swipe_down_does_nothing()
    {
        ThreeDown();
        MoveAll(0, 200, 100);

        Assert.Empty(_touch.FingerUp(1, 400, 700, 120));
    }

    [Fact]
    public void A_gesture_fires_once_as_the_fingers_lift()
    {
        ThreeDown();
        MoveAll(0, -200, 100);

        _touch.FingerUp(1, 400, 300, 120);

        Assert.Empty(_touch.FingerUp(2, 500, 300, 130));
        Assert.Empty(_touch.FingerUp(3, 600, 300, 140));
    }

    [Fact]
    public void Touch_works_again_after_a_gesture()
    {
        ThreeDown();
        MoveAll(0, -200, 100);
        _touch.FingerUp(1, 400, 300, 120);
        _touch.FingerUp(2, 500, 300, 130);
        _touch.FingerUp(3, 600, 300, 140);

        _touch.FingerDown(1, 100, 200, 200);

        Assert.Equal([GestureAction.Down, GestureAction.Up], Actions(_touch.FingerUp(1, 100, 200, 220)));
    }

    [Fact]
    public void A_third_finger_during_a_contact_releases_it_first()
    {
        _touch.FingerDown(1, 100, 200, 0);
        _touch.Tick(100);
        _touch.FingerDown(2, 300, 200, 200);

        Assert.Equal([GestureAction.Up], Actions(_touch.FingerDown(3, 500, 200, 210)));
    }

    [Fact]
    public void Four_fingers_are_not_treated_as_a_three_finger_gesture()
    {
        ThreeDown();
        _touch.FingerDown(4, 700, 500, 30);
        MoveAll(0, -200, 100);

        Assert.Empty(_touch.FingerUp(1, 400, 300, 120));
    }

    [Fact]
    public void Cancel_releases_an_active_contact()
    {
        _touch.FingerDown(1, 100, 200, 0);
        _touch.Tick(100);

        Assert.Equal([new TouchOutput(GestureAction.Up, 100, 200)], _touch.Cancel());
    }

    [Fact]
    public void Cancel_sends_nothing_when_no_contact_was_pressed()
    {
        _touch.FingerDown(1, 100, 200, 0);

        Assert.Empty(_touch.Cancel());
    }

    [Theory]
    [InlineData(0xFF515700L, true)]
    [InlineData(0xFF515780L, true)]
    [InlineData(0L, false)]
    [InlineData(0x12345678L, false)]
    public void Mouse_events_made_from_touch_are_recognized(long extraInfo, bool expected)
    {
        Assert.Equal(expected, InputCapture.IsTouchPromoted((IntPtr)extraInfo));
    }
}
