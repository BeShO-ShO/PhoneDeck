using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public class CastRetryWindowTests
{
    [Theory]
    [InlineData(0, 100_000, false)]          // the phone never dropped a cast
    [InlineData(100_000, 113_000, true)]     // 13 s after a drop, as on 26 Nov
    [InlineData(100_000, 160_000, true)]     // right at the end of the window
    [InlineData(100_000, 160_001, false)]    // too long ago
    [InlineData(100_000, 99_000, false)]     // a clock going backwards is ignored
    public void ListensOnlySoonAfterADrop(long droppedAt, long now, bool expected) =>
        Assert.Equal(expected, CastRetryWindow.ShouldListen(droppedAt, now));

    [Theory]
    [InlineData(0, 0, 0, 10_000)]            // just started: the full quiet time
    [InlineData(0, 0, 4_000, 6_000)]
    [InlineData(0, 0, 10_000, 0)]            // quiet for 10 s: stop
    [InlineData(0, 8_000, 9_000, 9_000)]     // a retry at 8 s waits until 18 s
    [InlineData(0, 25_000, 26_000, 4_000)]   // never past 30 s, however late the retry
    [InlineData(0, 25_000, 31_000, 0)]
    public void WaitsForTheQuietAfterTheLastRetry(long startedAt, long lastRetryAt, long now, int expected) =>
        Assert.Equal(expected, CastRetryWindow.RemainingMs(startedAt, lastRetryAt, now));
}
