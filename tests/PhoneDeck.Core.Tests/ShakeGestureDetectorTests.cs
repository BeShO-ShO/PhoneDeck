using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public sealed class ShakeGestureDetectorTests
{
    private static readonly SwitchGestureOptions Options =
        SwitchGestureOptions.ForTest(true, 80, 3, 800);

    /// <summary>
    /// Feeds alternating strokes along one axis and returns how many gestures fired. Each stroke
    /// is split into steps so the detector sees the same stream of small deltas a real mouse
    /// produces rather than one artificial jump.
    /// </summary>
    private static int FeedStrokes(ShakeGestureDetector detector, int strokes, int strokePx,
        bool horizontal = true, int stepPx = 10, int msPerStep = 5, int startTick = 1000)
    {
        var fires = 0;
        var now = startTick;
        var sign = 1;
        for (var stroke = 0; stroke < strokes; stroke++)
        {
            for (var moved = 0; moved < strokePx; moved += stepPx)
            {
                now += msPerStep;
                var delta = sign * stepPx;
                if (detector.Feed(horizontal ? delta : 0, horizontal ? 0 : delta, now)) fires++;
            }
            sign = -sign;
        }
        return fires;
    }

    [Fact]
    public void Shake_fires_once_the_required_reversals_arrive()
    {
        var detector = new ShakeGestureDetector(Options);

        Assert.Equal(1, FeedStrokes(detector, strokes: 4, strokePx: 100));
    }

    [Fact]
    public void Shake_fires_on_the_vertical_axis_too()
    {
        var detector = new ShakeGestureDetector(Options);

        Assert.Equal(1, FeedStrokes(detector, strokes: 4, strokePx: 100, horizontal: false));
    }

    [Fact]
    public void One_long_sweep_is_not_a_shake()
    {
        var detector = new ShakeGestureDetector(Options);

        Assert.Equal(0, FeedStrokes(detector, strokes: 1, strokePx: 2000));
    }

    [Fact]
    public void Strokes_shorter_than_the_minimum_are_treated_as_jitter()
    {
        var detector = new ShakeGestureDetector(Options);

        // Plenty of reversals, but none of them follow a deliberate stroke.
        Assert.Equal(0, FeedStrokes(detector, strokes: 20, strokePx: 40));
    }

    [Fact]
    public void Reversals_spread_beyond_the_window_never_add_up()
    {
        var detector = new ShakeGestureDetector(Options);

        // One stroke per sample, half a second apart: every reversal qualifies, but the third
        // one lands outside the window that started with the first.
        Assert.Equal(0, FeedStrokes(detector, strokes: 8, strokePx: 100, stepPx: 100, msPerStep: 500));
    }

    [Fact]
    public void A_single_long_shake_fires_only_once()
    {
        var detector = new ShakeGestureDetector(Options);

        // Twice the strokes needed, all inside the post-fire cooldown.
        Assert.Equal(1, FeedStrokes(detector, strokes: 8, strokePx: 100));
    }

    [Fact]
    public void A_second_shake_after_the_cooldown_fires_again()
    {
        var detector = new ShakeGestureDetector(Options);
        Assert.Equal(1, FeedStrokes(detector, strokes: 4, strokePx: 100, startTick: 1000));

        // The first run ends around t=1200; start well past the cooldown.
        Assert.Equal(1, FeedStrokes(detector, strokes: 4, strokePx: 100, startTick: 5000));
    }

    [Fact]
    public void Reset_drops_a_partial_gesture()
    {
        var detector = new ShakeGestureDetector(Options);
        var now = 1000;

        // Three strokes is two reversals - one short of firing.
        var sign = 1;
        for (var stroke = 0; stroke < 3; stroke++)
        {
            for (var step = 0; step < 10; step++)
            {
                now += 5;
                Assert.False(detector.Feed(sign * 10, 0, now));
            }
            sign = -sign;
        }

        detector.Reset();

        // The two strokes that follow would have completed the gesture had it survived.
        for (var stroke = 0; stroke < 2; stroke++)
        {
            for (var step = 0; step < 10; step++)
            {
                now += 5;
                Assert.False(detector.Feed(sign * 10, 0, now));
            }
            sign = -sign;
        }
    }

    [Fact]
    public void Jitter_on_the_other_axis_does_not_break_the_shake()
    {
        var detector = new ShakeGestureDetector(Options);
        var now = 1000;
        var fires = 0;
        var sign = 1;

        // A real horizontal shake drifts vertically, and near the turning points that drift
        // briefly dominates the sample.
        for (var stroke = 0; stroke < 4; stroke++)
        {
            for (var step = 0; step < 10; step++)
            {
                now += 5;
                var wobble = step % 2 == 0 ? 3 : -3;
                if (detector.Feed(sign * 10, wobble, now)) fires++;
            }
            sign = -sign;
        }

        Assert.Equal(1, fires);
    }
}
