using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public sealed class MultiTouchTrackerTests
{
    private readonly MultiTouchTracker _touch = new();

    private static RawContact Down(int id, double x, double y) => new(id, true, x, y);
    private static RawContact Up(int id) => new(id, false, 0, 0);
    private static bool Everywhere(double x, double y) => true;
    private static bool Nowhere(double x, double y) => false;

    private List<TrackedContact>? Feed(params RawContact[] frame) => _touch.Update(frame, Everywhere);

    [Fact]
    public void First_finger_presses_immediately_where_it_lands()
    {
        Assert.Equal([new TrackedContact(0, true, 10, 20)], Feed(Down(7, 10, 20)));
    }

    [Fact]
    public void Two_fingers_get_their_own_slots_in_one_frame()
    {
        Assert.Equal([new TrackedContact(0, true, 10, 20), new TrackedContact(1, true, 50, 60)],
            Feed(Down(7, 10, 20), Down(9, 50, 60)));
    }

    [Fact]
    public void A_pinch_moves_both_fingers_in_every_frame()
    {
        Feed(Down(7, 10, 20), Down(9, 50, 60));

        Assert.Equal([new TrackedContact(0, true, 12, 20), new TrackedContact(1, true, 48, 60)],
            Feed(Down(7, 12, 20), Down(9, 48, 60)));
    }

    [Fact]
    public void A_frame_where_nothing_moved_sends_nothing()
    {
        Feed(Down(7, 10, 20));

        Assert.Null(Feed(Down(7, 10, 20)));
    }

    [Fact]
    public void A_lift_is_sent_at_the_last_position_alongside_the_finger_still_down()
    {
        Feed(Down(7, 10, 20), Down(9, 50, 60));

        Assert.Equal([new TrackedContact(1, true, 50, 60), new TrackedContact(0, false, 10, 20)],
            Feed(Up(7), Down(9, 50, 60)));
    }

    [Fact]
    public void A_freed_slot_is_reused_by_the_next_finger()
    {
        Feed(Down(7, 10, 20));
        Feed(Up(7));

        Assert.Equal([new TrackedContact(0, true, 30, 30)], Feed(Down(8, 30, 30)));
    }

    [Fact]
    public void A_slot_lifting_in_a_frame_is_not_given_to_a_finger_landing_in_it()
    {
        Feed(Down(7, 10, 20));

        var frame = Feed(Up(7), Down(8, 30, 30));

        Assert.Equal([new TrackedContact(1, true, 30, 30), new TrackedContact(0, false, 10, 20)], frame);
    }

    [Fact]
    public void Fingers_beyond_the_limit_are_ignored_until_they_lift()
    {
        var touch = new MultiTouchTracker(maxContacts: 1);
        touch.Update([Down(1, 0, 0)], Everywhere);

        Assert.Null(touch.Update([Down(1, 0, 0), Down(2, 5, 5)], Everywhere));
        // Finger 1 lifts, but finger 2 was refused and stays ignored until it lifts too.
        Assert.Equal([new TrackedContact(0, false, 0, 0)], touch.Update([Up(1), Down(2, 6, 6)], Everywhere));
        Assert.Null(touch.Update([Down(2, 7, 7)], Everywhere));
    }

    [Fact]
    public void A_finger_starting_outside_the_video_is_ignored_even_when_it_slides_in()
    {
        Assert.Null(_touch.Update([Down(1, 10, 20)], Nowhere));
        Assert.Null(_touch.Update([Down(1, 10, 200)], Everywhere));
    }

    [Fact]
    public void A_drag_leaving_the_video_keeps_going()
    {
        Feed(Down(1, 10, 20));

        Assert.Equal([new TrackedContact(0, true, 10, 5)], _touch.Update([Down(1, 10, 5)], Nowhere));
    }

    [Fact]
    public void A_release_with_no_press_sends_nothing()
    {
        Assert.Null(Feed(Up(1)));
    }

    [Fact]
    public void Current_lists_the_fingers_held_down_for_the_keep_alive()
    {
        Feed(Down(7, 10, 20), Down(9, 50, 60));

        Assert.True(_touch.HasContacts);
        Assert.Equal([new TrackedContact(0, true, 10, 20), new TrackedContact(1, true, 50, 60)], _touch.Current());
    }

    [Fact]
    public void Cancel_lifts_every_finger()
    {
        Feed(Down(7, 10, 20), Down(9, 50, 60));

        Assert.Equal([new TrackedContact(0, false, 10, 20), new TrackedContact(1, false, 50, 60)], _touch.Cancel());
        Assert.False(_touch.HasContacts);
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
