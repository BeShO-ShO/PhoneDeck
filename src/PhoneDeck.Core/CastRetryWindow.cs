namespace PhoneDeck.Core;

/// <summary>
/// After a cast ends, the phone often casts again at once: when it dropped the cast by itself, and
/// also right after PhoneDeck told it to stop. If PhoneDeck's receiver is gone by then, Windows'
/// own receiver takes the retry and opens the Wireless Display app. So after ending a cast (on exit
/// or a change of route), and on exit soon after a drop, PhoneDeck keeps watching a while and
/// closes a Wireless Display app that starts.
/// </summary>
public static class CastRetryWindow
{
    /// <summary>How long after a drop the phone is assumed to still be retrying.</summary>
    public static readonly TimeSpan AfterDrop = TimeSpan.FromSeconds(60);

    /// <summary>Listening ends once the phone has been quiet this long.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);

    /// <summary>Listening never lasts longer than this, however often the phone retries.</summary>
    public static readonly TimeSpan MaxListen = TimeSpan.FromSeconds(30);

    /// <param name="droppedAt">Environment.TickCount64 when the phone dropped the cast; 0 for never.</param>
    /// <param name="now">Environment.TickCount64 now.</param>
    public static bool ShouldListen(long droppedAt, long now) =>
        droppedAt > 0 && now >= droppedAt && now - droppedAt <= (long)AfterDrop.TotalMilliseconds;

    /// <summary>
    /// How long to keep waiting, in milliseconds: until <see cref="Quiet"/> after the start or the
    /// last retry, and never past <see cref="MaxListen"/> from the start. 0 means stop now.
    /// </summary>
    public static int RemainingMs(long startedAt, long lastRetryAt, long now)
    {
        var quietEnds = Math.Max(startedAt, lastRetryAt) + (long)Quiet.TotalMilliseconds;
        var end = Math.Min(quietEnds, startedAt + (long)MaxListen.TotalMilliseconds);
        return (int)Math.Max(0, end - now);
    }
}
