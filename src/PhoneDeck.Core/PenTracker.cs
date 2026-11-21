namespace PhoneDeck.Core;

/// <summary>One pen report as read from the PC's digitizer; X, Y and pressure are 0-1 fractions
/// until the caller maps X and Y.</summary>
public readonly record struct RawPen(bool InRange, bool Tip, bool Barrel, bool Invert, bool Eraser,
    double X, double Y, double Pressure);

/// <summary>A pen state to send to the phone, in the caller's coordinates.</summary>
public readonly record struct PenOutput(PenFlags Flags, double X, double Y, double Pressure);

/// <summary>
/// Decides what the phone sees of the pen. Hovering or touching the video is sent; leaving the
/// video while hovering takes the pen out of range on the phone. A stroke that starts on the
/// video keeps going when it leaves, and one that starts outside is ignored until the tip lifts,
/// the same rules as fingers.
/// </summary>
public sealed class PenTracker
{
    private bool _active;
    private bool _blocked;
    private PenOutput _last;

    public PenOutput? Update(RawPen pen, bool insideVideo)
    {
        var tip = pen.Tip || pen.Eraser;

        if (_blocked)
        {
            if (tip && pen.InRange) return null;
            _blocked = false;
        }

        if (!_active)
        {
            if (!pen.InRange) return null;
            if (!insideVideo)
            {
                // Touching down outside the video belongs to whatever is there, not the phone.
                _blocked = tip;
                return null;
            }

            _active = true;
            return Emit(pen);
        }

        // Hovering off the video: the phone should see the pen go away, not stick at the edge.
        if (!pen.InRange || (!insideVideo && !tip)) return Leave();

        return Emit(pen);
    }

    /// <summary>Takes the pen out of range on the phone if it was in range.</summary>
    public PenOutput? Cancel()
    {
        _blocked = false;
        return _active ? Leave() : null;
    }

    private PenOutput? Leave()
    {
        _active = false;
        _last = new PenOutput(PenFlags.None, _last.X, _last.Y, 0);
        return _last;
    }

    private PenOutput? Emit(RawPen pen)
    {
        var flags = PenFlags.InRange;
        if (pen.Tip) flags |= PenFlags.Tip;
        if (pen.Barrel) flags |= PenFlags.Barrel;
        if (pen.Invert) flags |= PenFlags.Invert;
        if (pen.Eraser) flags |= PenFlags.Eraser | PenFlags.Invert;

        var output = new PenOutput(flags, pen.X, pen.Y, (flags & (PenFlags.Tip | PenFlags.Eraser)) != 0 ? pen.Pressure : 0);
        if (output == _last) return null;
        _last = output;
        return output;
    }
}
