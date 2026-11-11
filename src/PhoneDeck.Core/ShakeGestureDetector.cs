namespace PhoneDeck.Core;

/// <summary>
/// Recognises a deliberate back-and-forth shake of the pointer from a stream of relative deltas.
/// Relative motion is the one signal available in both directions: while input is local the
/// cursor position is known, but once it is going to a remote host the pointer is invisible to
/// us, so a gesture built on deltas alone works equally well whichever way the switch has to go.
/// </summary>
internal sealed class ShakeGestureDetector
{
    // One shake must not read as two. After firing, motion is ignored long enough for the user
    // to come to a stop, so the tail of the gesture cannot start another one.
    private const int CooldownMs = 500;

    private readonly SwitchGestureOptions _options;
    private readonly Axis _horizontal = new();
    private readonly Axis _vertical = new();
    private int _cooldownSince;
    private bool _inCooldown;

    public ShakeGestureDetector(SwitchGestureOptions options) => _options = options;

    /// <summary>
    /// Drops any partial gesture. The cooldown is deliberately preserved, because a target
    /// change is usually the result of a shake that is still being performed.
    /// </summary>
    public void Reset()
    {
        _horizontal.Reset();
        _vertical.Reset();
    }

    /// <summary>Feeds one pointer delta. Returns true exactly once per recognised shake.</summary>
    public bool Feed(int dx, int dy, int now)
    {
        if (_inCooldown)
        {
            if (unchecked(now - _cooldownSince) < CooldownMs) return false;
            _inCooldown = false;
        }

        // The two axes are tracked independently rather than routing each sample to whichever
        // axis dominates it. A horizontal shake still carries vertical jitter, and near the
        // turning points that jitter briefly dominates; letting it steal samples would break
        // the chain at exactly the wrong moment.
        var fired = _horizontal.Feed(dx, now, _options);
        fired |= _vertical.Feed(dy, now, _options);
        if (!fired) return false;

        Reset();
        _cooldownSince = now;
        _inCooldown = true;
        return true;
    }

    private sealed class Axis
    {
        private int _direction;
        private int _travel;
        private int _reversals;
        private int _firstReversalTick;

        public void Reset()
        {
            _direction = 0;
            _travel = 0;
            _reversals = 0;
            _firstReversalTick = 0;
        }

        public bool Feed(int delta, int now, SwitchGestureOptions options)
        {
            if (delta == 0) return false;

            // Reversals only add up while they stay inside the window; an older chain is stale,
            // so this reversal starts a fresh one instead of completing it.
            if (_reversals > 0 && unchecked(now - _firstReversalTick) > options.WindowMs)
                _reversals = 0;

            var sign = Math.Sign(delta);
            if (sign == _direction)
            {
                _travel += Math.Abs(delta);
                return false;
            }

            // A reversal counts only when the stroke leading into it was long enough to be
            // deliberate. Anything shorter is jitter, and it breaks the chain so that noise can
            // never accumulate into a gesture.
            var qualified = _direction != 0 && _travel >= options.MinStrokePx;
            _direction = sign;
            _travel = Math.Abs(delta);

            if (!qualified)
            {
                _reversals = 0;
                return false;
            }

            if (_reversals == 0) _firstReversalTick = now;
            _reversals++;
            return _reversals >= options.RequiredReversals;
        }
    }
}
