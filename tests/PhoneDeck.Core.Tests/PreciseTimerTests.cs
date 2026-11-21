using System.Diagnostics;
using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public sealed class PreciseTimerTests
{
    [Fact]
    public void Uses_a_high_resolution_timer_on_supported_windows()
    {
        using var timer = new PreciseTimer();
        // Windows 10 1803 and later, which covers every supported version.
        Assert.True(timer.IsHighResolution);
    }

    [Fact]
    public async Task Waits_at_least_the_requested_time()
    {
        using var timer = new PreciseTimer();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) await timer.WaitAsync(8);
        clock.Stop();

        // Loose upper bound: CI machines are noisy, but five 8 ms waits must not take seconds.
        Assert.InRange(clock.Elapsed.TotalMilliseconds, 5 * 8 - 1, 1000);
    }

    [Fact]
    public async Task Zero_or_negative_waits_complete_immediately()
    {
        using var timer = new PreciseTimer();
        Assert.True(timer.WaitAsync(0).IsCompleted);
        Assert.True(timer.WaitAsync(-3).IsCompleted);
        await timer.WaitAsync(1);
    }

    [Fact]
    public async Task Cancellation_ends_a_pending_wait()
    {
        using var timer = new PreciseTimer();
        using var cancel = new CancellationTokenSource();
        var wait = timer.WaitAsync(10_000, cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        // The timer stays usable after a cancelled wait.
        await timer.WaitAsync(2);
    }

    [Fact]
    public async Task An_already_cancelled_token_throws()
    {
        using var timer = new PreciseTimer();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => timer.WaitAsync(5, new CancellationToken(canceled: true)));
    }
}
