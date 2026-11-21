using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public sealed class PointerResamplerTests
{
    [Fact]
    public void Steady_input_becomes_evenly_sized_reports()
    {
        // A 125 Hz mouse moving 5 px per event, read every 15 ms. Plain coalescing gives
        // alternating 5 and 10 px reports here; resampling gives 9 or 10 every time.
        var resampler = new PointerResampler();
        var reports = new List<int>();
        var nextInput = 0.0;
        for (var tick = 15.0; tick <= 600; tick += 15)
        {
            for (; nextInput <= tick; nextInput += 8) resampler.Add(nextInput, 5, 0);
            reports.Add(resampler.Take(tick).Dx);
        }

        // Skip the start of the movement, where the first report is only partly due.
        Assert.All(reports.Skip(2), dx => Assert.InRange(dx, 9, 10));
    }

    [Fact]
    public void Plain_coalescing_of_the_same_input_is_uneven()
    {
        // The problem the resampler solves, shown with the same timings.
        var sums = new List<int>();
        var nextInput = 0.0;
        for (var tick = 15.0; tick <= 600; tick += 15)
        {
            var sum = 0;
            for (; nextInput <= tick; nextInput += 8) sum += 5;
            sums.Add(sum);
        }

        Assert.Contains(5, sums);
        Assert.Contains(10, sums);
    }

    [Fact]
    public void All_motion_is_delivered_shortly_after_input_stops()
    {
        var resampler = new PointerResampler();
        var random = new Random(7);
        int inX = 0, inY = 0, outX = 0, outY = 0;
        var now = 0.0;
        for (var i = 0; i < 200; i++)
        {
            now += random.Next(1, 12);
            var dx = random.Next(-9, 10);
            var dy = random.Next(-9, 10);
            resampler.Add(now, dx, dy);
            inX += dx;
            inY += dy;
            if (i % 3 == 0)
            {
                var (x, y) = resampler.Take(now);
                outX += x;
                outY += y;
            }
        }

        var (lastX, lastY) = resampler.Take(now + PointerResampler.MaxDelayMs + 1);
        Assert.Equal((inX, inY), (outX + lastX, outY + lastY));
        Assert.False(resampler.HasPending);
    }

    [Fact]
    public void The_first_event_after_idle_is_not_spread_over_the_idle_time()
    {
        var resampler = new PointerResampler();
        resampler.Add(0, 3, 0);
        Assert.Equal(3, resampler.Take(100).Dx);

        // Five seconds later: the move must arrive within the reading delay, not seconds later.
        resampler.Add(5000, 12, -4);
        var total = resampler.Take(5000 + PointerResampler.MaxDelayMs);
        Assert.Equal((12, -4), total);
    }

    [Fact]
    public void Nothing_is_due_the_instant_a_movement_starts()
    {
        var resampler = new PointerResampler();
        resampler.Add(1000, 10, 0);
        Assert.Equal((0, 0), resampler.Take(1000));
        Assert.True(resampler.HasPending);
    }

    [Fact]
    public void Flush_hands_out_everything_at_once()
    {
        var resampler = new PointerResampler();
        resampler.Add(0, 7, 2);
        resampler.Add(8, 7, 2);
        var partial = resampler.Take(8);
        var rest = resampler.Flush();

        Assert.Equal((14, 4), (partial.Dx + rest.Dx, partial.Dy + rest.Dy));
        Assert.False(resampler.HasPending);
        Assert.Equal((0, 0), resampler.Flush());
    }

    [Fact]
    public void Sub_pixel_motion_is_carried_not_lost()
    {
        // One pixel per 8 ms event, read every 3 ms: most reads are fractions of a pixel.
        var resampler = new PointerResampler();
        var total = 0;
        var events = 0;
        var nextInput = 0.0;
        for (var tick = 0.0; tick <= 400; tick += 3)
        {
            for (; nextInput <= tick; nextInput += 8, events++) resampler.Add(nextInput, -1, 0);
            total += resampler.Take(tick).Dx;
        }
        total += resampler.Flush().Dx;

        Assert.Equal(-events, total);
    }

    [Fact]
    public void Reads_never_go_back_in_time()
    {
        var resampler = new PointerResampler();
        resampler.Add(0, 20, 0);
        resampler.Add(8, 20, 0);
        var first = resampler.Take(14);
        // A read with an earlier clock (or a longer delay) must not hand back negative motion.
        var second = resampler.Take(10);

        Assert.True(first.Dx > 0);
        Assert.Equal(0, second.Dx);
    }
}
