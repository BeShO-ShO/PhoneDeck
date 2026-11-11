namespace PhoneDeck.Core;

public enum TouchPhase { Down, Move, Up }

public readonly record struct TouchOutput(TouchPhase Phase, double X, double Y);

/// <summary>
/// Reduces a touchscreen's contacts to one finger. The first finger to land on the video is the
/// touch; any other finger is ignored until it lifts, even if the first has already gone.
/// </summary>
public sealed class SingleTouchTracker
{
    private readonly HashSet<int> _ignored = [];
    private int? _active;
    private (double X, double Y) _last;

    public TouchOutput? Update(int id, bool touching, double x, double y, bool insideVideo)
    {
        if (id == _active)
        {
            if (!touching)
            {
                _active = null;
                return new(TouchPhase.Up, _last.X, _last.Y);
            }

            if ((x, y) == _last) return null;
            _last = (x, y);
            return new(TouchPhase.Move, x, y);
        }

        if (!touching)
        {
            _ignored.Remove(id);
            return null;
        }

        if (_active is not null || _ignored.Contains(id) || !insideVideo)
        {
            _ignored.Add(id);
            return null;
        }

        _active = id;
        _last = (x, y);
        return new(TouchPhase.Down, x, y);
    }

    /// <summary>Ends anything in progress, so a contact is never left pressed on the phone.</summary>
    public TouchOutput? Cancel()
    {
        _ignored.Clear();
        if (_active is null) return null;
        _active = null;
        return new(TouchPhase.Up, _last.X, _last.Y);
    }
}
