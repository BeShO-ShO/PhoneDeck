namespace BleHid.Core;

public enum GestureAction { Down, Move, Up, Back, Home, Recents }

public readonly record struct TouchOutput(GestureAction Action, double X = 0, double Y = 0);

/// <summary>
/// Turns raw fingers into what the phone should see. One or two fingers become a single touch
/// contact, which scrolls under the fingers. Three fingers become navigation gestures and send
/// no touch at all, which is why a new contact is held back briefly: three fingers never land
/// at the same instant, and a first finger already sent would register as a stray tap.
/// </summary>
public sealed class TouchGestureRecognizer
{
    private const long PendingMs = 70;
    private const double MoveSlop = 12;
    private const double SwipeDistance = 80;
    private const long HoldMs = 350;
    private const long ThreeFingerTapMs = 300;
    private const double GestureMoveStep = 6;

    private enum State { Idle, Pending, Contact, Gesture, Ignoring }

    private readonly Dictionary<int, (double X, double Y)> _fingers = [];
    private State _state = State.Idle;
    private long _pendingSince;
    private (double X, double Y) _pendingStart;
    private (double X, double Y) _contact;
    private (double X, double Y) _offset;
    private (double X, double Y) _gestureStart;
    private (double X, double Y) _gestureLast;
    private long _gestureStartedAt;
    private long _gestureMovedAt;

    public List<TouchOutput> FingerDown(int id, double x, double y, long now)
    {
        var output = new List<TouchOutput>();
        _fingers[id] = (x, y);

        switch (_state)
        {
            case State.Idle:
                _state = State.Pending;
                _pendingSince = now;
                _pendingStart = (x, y);
                break;

            case State.Pending:
                if (_fingers.Count >= 3) BeginGesture(now);
                else _pendingStart = Centroid();
                break;

            case State.Contact:
                if (_fingers.Count >= 3)
                {
                    output.Add(new(GestureAction.Up, _contact.X, _contact.Y));
                    BeginGesture(now);
                }
                else Rebase();
                break;

            case State.Gesture:
                // Four fingers mean something else on Android, so this is not a three-finger gesture.
                _state = State.Ignoring;
                break;
        }

        return output;
    }

    public List<TouchOutput> FingerMove(int id, double x, double y, long now)
    {
        var output = new List<TouchOutput>();
        if (!_fingers.ContainsKey(id)) return output;
        _fingers[id] = (x, y);
        var centroid = Centroid();

        switch (_state)
        {
            case State.Pending:
                if (Distance(centroid, _pendingStart) <= MoveSlop) break;
                output.Add(new(GestureAction.Down, _pendingStart.X, _pendingStart.Y));
                _state = State.Contact;
                _offset = (0, 0);
                _contact = centroid;
                output.Add(new(GestureAction.Move, _contact.X, _contact.Y));
                break;

            case State.Contact:
                _contact = (centroid.X + _offset.X, centroid.Y + _offset.Y);
                output.Add(new(GestureAction.Move, _contact.X, _contact.Y));
                break;

            case State.Gesture:
                if (Distance(centroid, _gestureLast) <= GestureMoveStep) break;
                _gestureLast = centroid;
                _gestureMovedAt = now;
                break;
        }

        return output;
    }

    public List<TouchOutput> FingerUp(int id, double x, double y, long now)
    {
        var output = new List<TouchOutput>();
        if (!_fingers.ContainsKey(id)) return output;
        _fingers[id] = (x, y);

        // Judged before removing the finger: the centroid jumps once one has lifted.
        var gesture = _state == State.Gesture ? Classify(Centroid(), now) : null;
        _fingers.Remove(id);
        var remaining = _fingers.Count;

        switch (_state)
        {
            case State.Pending:
                if (remaining > 0)
                {
                    _pendingStart = Centroid();
                    break;
                }
                output.Add(new(GestureAction.Down, _pendingStart.X, _pendingStart.Y));
                output.Add(new(GestureAction.Up, _pendingStart.X, _pendingStart.Y));
                _state = State.Idle;
                break;

            case State.Contact:
                if (remaining > 0)
                {
                    Rebase();
                    break;
                }
                output.Add(new(GestureAction.Up, _contact.X, _contact.Y));
                _state = State.Idle;
                break;

            case State.Gesture:
                if (gesture is { } action) output.Add(new(action));
                _state = remaining > 0 ? State.Ignoring : State.Idle;
                break;

            case State.Ignoring:
                if (remaining == 0) _state = State.Idle;
                break;
        }

        return output;
    }

    /// <summary>Call regularly: a finger held still has no other event to release it.</summary>
    public List<TouchOutput> Tick(long now)
    {
        var output = new List<TouchOutput>();
        if (_state != State.Pending || now - _pendingSince < PendingMs) return output;

        output.Add(new(GestureAction.Down, _pendingStart.X, _pendingStart.Y));
        _state = State.Contact;
        _offset = (0, 0);
        _contact = _pendingStart;
        Rebase();
        return output;
    }

    /// <summary>Ends anything in progress, so a contact is never left pressed on the phone.</summary>
    public List<TouchOutput> Cancel()
    {
        var output = new List<TouchOutput>();
        if (_state == State.Contact) output.Add(new(GestureAction.Up, _contact.X, _contact.Y));
        _fingers.Clear();
        _state = State.Idle;
        return output;
    }

    private void BeginGesture(long now)
    {
        _state = State.Gesture;
        _gestureStart = _gestureLast = Centroid();
        _gestureStartedAt = _gestureMovedAt = now;
    }

    // A finger landing or lifting moves the centroid without anyone having dragged anything.
    private void Rebase()
    {
        var centroid = Centroid();
        _offset = (_contact.X - centroid.X, _contact.Y - centroid.Y);
    }

    private GestureAction? Classify((double X, double Y) centroid, long now)
    {
        var dx = centroid.X - _gestureStart.X;
        var dy = centroid.Y - _gestureStart.Y;

        if (Math.Abs(dx) < SwipeDistance && Math.Abs(dy) < SwipeDistance)
        {
            var isTap = Distance(centroid, _gestureStart) <= MoveSlop && now - _gestureStartedAt <= ThreeFingerTapMs;
            return isTap ? GestureAction.Back : null;
        }

        if (Math.Abs(dx) > Math.Abs(dy)) return GestureAction.Back;
        if (dy > 0) return null;
        return now - _gestureMovedAt >= HoldMs ? GestureAction.Recents : GestureAction.Home;
    }

    private (double X, double Y) Centroid() =>
        (_fingers.Values.Average(f => f.X), _fingers.Values.Average(f => f.Y));

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
