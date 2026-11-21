namespace PhoneDeck.Core;

/// <summary>
/// Turns bursty mouse motion into evenly sized reports.
/// <para>
/// The mouse reports every few milliseconds, but reports go to the phone once per link
/// interval, so plain coalescing hands the phone one input event in one report and two in
/// the next. Android's pointer acceleration is speed-based, so that alternating step size
/// becomes an alternating pointer speed: the pointer jumps and overshoots even when the hand
/// moves steadily.
/// </para>
/// <para>
/// Instead, each input event's motion is treated as spread evenly over the time since the
/// previous event (its "span"), and reports read that motion curve a short delay behind real
/// time. Steady hand motion then becomes a straight line, and every report gets an equal
/// share of it, however input and report timing beat against each other. The delay is about
/// one input period (a few milliseconds); motion is never lost or invented, and the
/// remainders of fractional pixels carry forward.
/// </para>
/// Not thread-safe: the caller serialises access.
/// </summary>
public sealed class PointerResampler
{
    /// <summary>A gap longer than this is a new movement, not a slow part of the same one.</summary>
    public const double MaxSpanMs = 20;

    /// <summary>The input period assumed before any has been measured (a 125 Hz mouse).</summary>
    public const double DefaultPeriodMs = 8;

    /// <summary>The reading delay never exceeds this, so smoothing cannot add more lag.</summary>
    public const double MaxDelayMs = 12;

    private readonly record struct Motion(double Start, double End, int Dx, int Dy);

    private readonly List<Motion> _pending = [];
    private double _periodMs = DefaultPeriodMs;
    private double _lastInputMs = double.NegativeInfinity;
    private double _lastSampleMs = double.NegativeInfinity;

    // Motion already completed and folded out of _pending, and what has been handed out.
    private long _settledX, _settledY;
    private long _sentX, _sentY;

    /// <summary>The smoothed input period, which is also the reading delay.</summary>
    public double DelayMs => Math.Min(_periodMs, MaxDelayMs);

    /// <summary>True while some motion has not been handed out yet.</summary>
    public bool HasPending => _pending.Count > 0 || _settledX != _sentX || _settledY != _sentY;

    public void Add(double nowMs, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;

        var gap = nowMs - _lastInputMs;
        double span;
        if (gap > 0 && gap <= MaxSpanMs)
        {
            span = gap;
            // Slow average so one late event does not swing the delay.
            _periodMs += (gap - _periodMs) * 0.2;
        }
        else
        {
            // First event of a movement (or two events in the same millisecond): assume the
            // usual period rather than stretching it back over the idle time.
            span = gap > MaxSpanMs || double.IsInfinity(gap) ? _periodMs : Math.Max(gap, 0.5);
        }

        _lastInputMs = Math.Max(_lastInputMs, nowMs);
        _pending.Add(new Motion(nowMs - span, nowMs, dx, dy));
    }

    /// <summary>The whole-pixel motion due by <paramref name="nowMs"/>; (0, 0) when none is.</summary>
    public (int Dx, int Dy) Take(double nowMs)
    {
        var sample = Math.Max(_lastSampleMs, nowMs - DelayMs);
        _lastSampleMs = sample;

        double partialX = 0, partialY = 0;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var motion = _pending[i];
            if (sample >= motion.End)
            {
                _settledX += motion.Dx;
                _settledY += motion.Dy;
                _pending.RemoveAt(i);
                continue;
            }

            var length = motion.End - motion.Start;
            var fraction = length <= 0 ? 0 : Math.Clamp((sample - motion.Start) / length, 0, 1);
            partialX += motion.Dx * fraction;
            partialY += motion.Dy * fraction;
        }

        return Emit(_settledX + partialX, _settledY + partialY);
    }

    /// <summary>Everything not yet handed out, for a click or target change that must follow it.</summary>
    public (int Dx, int Dy) Flush()
    {
        foreach (var motion in _pending)
        {
            _settledX += motion.Dx;
            _settledY += motion.Dy;
        }
        _pending.Clear();
        return Emit(_settledX, _settledY);
    }

    private (int Dx, int Dy) Emit(double targetX, double targetY)
    {
        var dx = (int)(Math.Round(targetX, MidpointRounding.AwayFromZero) - _sentX);
        var dy = (int)(Math.Round(targetY, MidpointRounding.AwayFromZero) - _sentY);
        _sentX += dx;
        _sentY += dy;
        return (dx, dy);
    }
}
