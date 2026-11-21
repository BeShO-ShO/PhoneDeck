using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public sealed class PenTrackerTests
{
    private readonly PenTracker _pen = new();

    private static RawPen Hover(double x = 10, double y = 20) => new(true, false, false, false, false, x, y, 0);
    private static RawPen Draw(double x = 10, double y = 20, double pressure = 0.5) => new(true, true, false, false, false, x, y, pressure);
    private static readonly RawPen Away = new(false, false, false, false, false, 0, 0, 0);

    [Fact]
    public void Hovering_over_the_video_is_sent_in_range_without_pressure()
    {
        Assert.Equal(new PenOutput(PenFlags.InRange, 10, 20, 0), _pen.Update(Hover() with { Pressure = 0.3 }, true));
    }

    [Fact]
    public void Drawing_sends_tip_and_pressure()
    {
        _pen.Update(Hover(), true);

        Assert.Equal(new PenOutput(PenFlags.InRange | PenFlags.Tip, 11, 21, 0.5), _pen.Update(Draw(11, 21), true));
    }

    [Fact]
    public void An_unchanged_report_sends_nothing()
    {
        _pen.Update(Hover(), true);

        Assert.Null(_pen.Update(Hover(), true));
    }

    [Fact]
    public void Lifting_the_pen_away_takes_it_out_of_range()
    {
        _pen.Update(Hover(), true);

        Assert.Equal(new PenOutput(PenFlags.None, 10, 20, 0), _pen.Update(Away, true));
        Assert.Null(_pen.Update(Away, true));
    }

    [Fact]
    public void Hovering_off_the_video_takes_it_out_of_range()
    {
        _pen.Update(Hover(), true);

        Assert.Equal(PenFlags.None, _pen.Update(Hover(5, 5), false)?.Flags);
    }

    [Fact]
    public void A_stroke_leaving_the_video_keeps_drawing()
    {
        _pen.Update(Draw(), true);

        Assert.Equal(PenFlags.InRange | PenFlags.Tip, _pen.Update(Draw(5, 5), false)?.Flags);
    }

    [Fact]
    public void A_stroke_starting_outside_is_ignored_until_the_tip_lifts()
    {
        Assert.Null(_pen.Update(Draw(), false));
        Assert.Null(_pen.Update(Draw(30, 30), true));

        Assert.Equal(PenFlags.InRange, _pen.Update(Hover(30, 30), true)?.Flags);
    }

    [Fact]
    public void The_eraser_end_is_sent_as_invert_and_erasing_as_eraser()
    {
        var invert = new RawPen(true, false, false, true, false, 1, 1, 0);
        var erase = new RawPen(true, false, false, true, true, 1, 1, 0.8);

        Assert.Equal(PenFlags.InRange | PenFlags.Invert, _pen.Update(invert, true)?.Flags);
        Assert.Equal(new PenOutput(PenFlags.InRange | PenFlags.Invert | PenFlags.Eraser, 1, 1, 0.8), _pen.Update(erase, true));
    }

    [Fact]
    public void Cancel_takes_the_pen_away_only_if_it_was_in_range()
    {
        Assert.Null(_pen.Cancel());
        _pen.Update(Draw(), true);

        Assert.Equal(PenFlags.None, _pen.Cancel()?.Flags);
    }
}
