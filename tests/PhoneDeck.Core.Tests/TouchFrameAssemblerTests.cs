using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public sealed class TouchFrameAssemblerTests
{
    private readonly TouchFrameAssembler _frames = new();

    private static RawContact Down(int id, double x = 1, double y = 2) => new(id, true, x, y);
    private static RawContact Lifted(int id) => new(id, false, 1, 2);
    private static readonly RawContact[] NoSlots = [];

    [Fact]
    public void A_complete_frame_passes_its_contacts_through()
    {
        Assert.Equal([Down(1)], _frames.Add(1, [Down(1)]));
    }

    [Fact]
    public void Slots_beyond_the_contact_count_are_stale_and_dropped()
    {
        Assert.Equal([Down(1)], _frames.Add(1, [Down(1), Down(7), Down(8)]));
    }

    [Fact]
    public void A_frame_split_across_reports_waits_for_all_of_it()
    {
        Assert.Empty(_frames.Add(2, [Down(1)]));

        Assert.Equal([Down(1), Down(2)], _frames.Add(0, [Down(2)]));
    }

    [Fact]
    public void A_reported_release_passes_through()
    {
        _frames.Add(1, [Down(1)]);

        Assert.Equal([Lifted(1)], _frames.Add(1, [Lifted(1)]));
    }

    [Fact]
    public void A_finger_missing_from_a_frame_is_released()
    {
        _frames.Add(2, [Down(1), Down(2)]);

        var output = _frames.Add(1, [Down(2)]);

        Assert.Equal([new RawContact(1, false, 0, 0), Down(2)], output);
    }

    [Fact]
    public void The_lost_finger_is_released_before_a_new_one_lands()
    {
        _frames.Add(1, [Down(1)]);

        var output = _frames.Add(1, [Down(2)]);

        Assert.Equal([new RawContact(1, false, 0, 0), Down(2)], output);
    }

    [Fact]
    public void An_empty_report_releases_everything()
    {
        _frames.Add(2, [Down(1), Down(2)]);

        var output = _frames.Add(0, NoSlots);

        Assert.Equal(2, output.Count);
        Assert.All(output, contact => Assert.False(contact.Touching));
    }

    [Fact]
    public void A_release_is_only_sent_once()
    {
        _frames.Add(1, [Down(1)]);
        _frames.Add(0, NoSlots);

        Assert.Empty(_frames.Add(0, NoSlots));
    }

    [Fact]
    public void A_device_with_no_contact_count_passes_every_slot_through()
    {
        Assert.Equal([Down(1), Lifted(2)], _frames.Add(null, [Down(1), Lifted(2)]));
    }

    [Fact]
    public void Lost_finger_no_longer_freezes_the_tracker()
    {
        var tracker = new SingleTouchTracker();
        void Feed(int? count, params RawContact[] slots)
        {
            foreach (var contact in _frames.Add(count, slots))
                tracker.Update(contact.Id, contact.Touching, contact.X, contact.Y, true);
        }

        Feed(1, Down(1));
        // Finger 1 vanishes without a release. Finger 2 lands, then moves.
        Feed(1, Down(2, 10, 10));

        var moved = _frames.Add(1, [Down(2, 20, 20)]).Single();

        Assert.Equal(TouchPhase.Move, tracker.Update(moved.Id, moved.Touching, moved.X, moved.Y, true)?.Phase);
    }
}
