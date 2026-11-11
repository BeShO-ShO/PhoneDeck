using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public sealed class SingleTouchTrackerTests
{
    private readonly SingleTouchTracker _touch = new();

    [Fact]
    public void First_finger_presses_immediately_where_it_lands()
    {
        Assert.Equal(new TouchOutput(TouchPhase.Down, 10, 20), _touch.Update(1, true, 10, 20, true));
    }

    [Fact]
    public void Moving_reports_the_new_position()
    {
        _touch.Update(1, true, 10, 20, true);

        Assert.Equal(new TouchOutput(TouchPhase.Move, 15, 25), _touch.Update(1, true, 15, 25, true));
    }

    [Fact]
    public void An_unchanged_position_sends_nothing()
    {
        _touch.Update(1, true, 10, 20, true);

        Assert.Null(_touch.Update(1, true, 10, 20, true));
    }

    [Fact]
    public void Lifting_releases_at_the_last_position()
    {
        _touch.Update(1, true, 10, 20, true);
        _touch.Update(1, true, 15, 25, true);

        Assert.Equal(new TouchOutput(TouchPhase.Up, 15, 25), _touch.Update(1, false, 99, 99, true));
    }

    [Fact]
    public void A_second_finger_is_ignored()
    {
        _touch.Update(1, true, 10, 20, true);

        Assert.Null(_touch.Update(2, true, 50, 60, true));
        Assert.Null(_touch.Update(2, true, 55, 65, true));
        Assert.Null(_touch.Update(2, false, 55, 65, true));
    }

    [Fact]
    public void A_second_finger_does_not_take_over_when_the_first_lifts()
    {
        _touch.Update(1, true, 10, 20, true);
        _touch.Update(2, true, 50, 60, true);
        _touch.Update(1, false, 10, 20, true);

        Assert.Null(_touch.Update(2, true, 55, 65, true));
    }

    [Fact]
    public void A_finger_can_press_again_after_lifting_while_ignored()
    {
        _touch.Update(1, true, 10, 20, true);
        _touch.Update(2, true, 50, 60, true);
        _touch.Update(1, false, 10, 20, true);
        _touch.Update(2, false, 50, 60, true);

        Assert.Equal(TouchPhase.Down, _touch.Update(2, true, 70, 80, true)?.Phase);
    }

    [Fact]
    public void A_finger_starting_outside_the_video_is_ignored()
    {
        Assert.Null(_touch.Update(1, true, 10, 20, false));
    }

    [Fact]
    public void A_finger_sliding_in_from_outside_the_video_does_not_press()
    {
        _touch.Update(1, true, 10, 20, false);

        Assert.Null(_touch.Update(1, true, 10, 200, true));
    }

    [Fact]
    public void A_drag_leaving_the_video_keeps_going()
    {
        _touch.Update(1, true, 10, 20, true);

        Assert.Equal(TouchPhase.Move, _touch.Update(1, true, 10, 5, false)?.Phase);
    }

    [Fact]
    public void A_release_with_no_press_sends_nothing()
    {
        Assert.Null(_touch.Update(1, false, 10, 20, true));
    }

    [Fact]
    public void Cancel_releases_an_active_touch()
    {
        _touch.Update(1, true, 10, 20, true);

        Assert.Equal(new TouchOutput(TouchPhase.Up, 10, 20), _touch.Cancel());
    }

    [Fact]
    public void Cancel_sends_nothing_when_idle()
    {
        Assert.Null(_touch.Cancel());
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
