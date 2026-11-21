namespace PhoneDeck.Core;

/// <summary>A finger in the frame sent to the phone, in the caller's coordinates.</summary>
public readonly record struct TrackedContact(int Slot, bool Down, double X, double Y);

/// <summary>
/// Turns a touchscreen's contacts into the fingers sent to the phone. A finger counts only if it
/// lands on the video; one that lands outside (on the button bar, say) is ignored until it lifts,
/// even if it slides onto the video. Once down, a finger keeps going when dragged off the video.
/// Each finger gets a small slot number, reused after it lifts, which becomes its contact id.
/// </summary>
public sealed class MultiTouchTracker
{
    private readonly int _maxContacts;
    private readonly Dictionary<int, (int Slot, double X, double Y)> _active = [];
    private readonly HashSet<int> _ignored = [];

    public MultiTouchTracker(int maxContacts = HidReports.MaxTouchContacts) => _maxContacts = maxContacts;

    public bool HasContacts => _active.Count > 0;

    /// <summary>
    /// Applies one frame of raw contacts. Returns the frame to send, every finger still down plus
    /// any that lifted in this frame, or null when nothing changed.
    /// </summary>
    public List<TrackedContact>? Update(IReadOnlyList<RawContact> frame, Func<double, double, bool> insideVideo)
    {
        var lifted = new List<TrackedContact>();
        var changed = false;

        foreach (var contact in frame)
        {
            if (_active.TryGetValue(contact.Id, out var known))
            {
                if (!contact.Touching)
                {
                    _active.Remove(contact.Id);
                    lifted.Add(new TrackedContact(known.Slot, false, known.X, known.Y));
                    changed = true;
                }
                else if ((contact.X, contact.Y) != (known.X, known.Y))
                {
                    _active[contact.Id] = (known.Slot, contact.X, contact.Y);
                    changed = true;
                }
                continue;
            }

            if (!contact.Touching)
            {
                _ignored.Remove(contact.Id);
                continue;
            }

            if (_ignored.Contains(contact.Id) || _active.Count >= _maxContacts || !insideVideo(contact.X, contact.Y))
            {
                _ignored.Add(contact.Id);
                continue;
            }

            // A slot lifting in this frame is still in it, so it cannot be handed out again yet.
            _active[contact.Id] = (FreeSlot(lifted), contact.X, contact.Y);
            changed = true;
        }

        if (!changed) return null;

        // Lifts go after the fingers still down; the order inside a frame does not matter to the
        // host, but a stable order keeps the frames easy to read in tests and logs.
        var output = Current();
        output.AddRange(lifted.OrderBy(contact => contact.Slot));
        return output;
    }

    /// <summary>Every finger currently down, by slot. Resent while fingers are held still,
    /// because the host releases a finger it has not heard about for a moment.</summary>
    public List<TrackedContact> Current() => _active.Values
        .OrderBy(value => value.Slot)
        .Select(value => new TrackedContact(value.Slot, true, value.X, value.Y))
        .ToList();

    /// <summary>Lifts everything, so no finger is ever left pressed on the phone.</summary>
    public List<TrackedContact>? Cancel()
    {
        _ignored.Clear();
        if (_active.Count == 0) return null;

        var lifted = _active.Values
            .OrderBy(value => value.Slot)
            .Select(value => new TrackedContact(value.Slot, false, value.X, value.Y))
            .ToList();
        _active.Clear();
        return lifted;
    }

    private int FreeSlot(List<TrackedContact> liftedThisFrame)
    {
        var used = _active.Values.Select(value => value.Slot).ToHashSet();
        used.UnionWith(liftedThisFrame.Select(contact => contact.Slot));
        var slot = 0;
        while (used.Contains(slot)) slot++;
        return slot;
    }
}
