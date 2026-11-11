namespace BleHid.Core;

public readonly record struct RawContact(int Id, bool Touching, double X, double Y);

/// <summary>
/// Groups a touchscreen's reports into whole frames. A report carries a fixed number of contact
/// slots and a contact count saying how many are real, so the rest hold stale data. A frame can
/// also span several reports. Any finger missing from a complete frame has gone, even if its
/// release was never reported, which is what stops a lost finger freezing touch.
/// </summary>
public sealed class TouchFrameAssembler
{
    private readonly HashSet<int> _live = [];
    private readonly List<RawContact> _frame = [];
    private int _expected;

    /// <param name="contactCount">
    /// The report's contact count: positive starts a frame, zero continues one or, with none
    /// open, means nothing is touching. Null if the device has no contact count.
    /// </param>
    public List<RawContact> Add(int? contactCount, IReadOnlyList<RawContact> slots)
    {
        if (contactCount is null) return slots.ToList();

        if (contactCount > 0)
        {
            _frame.Clear();
            _expected = contactCount.Value;
        }
        else if (_expected == 0)
        {
            return CloseFrame();
        }

        foreach (var slot in slots)
        {
            if (_frame.Count == _expected) break;
            _frame.Add(slot);
        }

        return _frame.Count < _expected ? [] : CloseFrame();
    }

    private List<RawContact> CloseFrame()
    {
        var touching = _frame.Where(contact => contact.Touching).Select(contact => contact.Id).ToHashSet();

        // Releases go first, so a new finger in this frame is not refused because of a lost one.
        var output = _live.Where(id => !touching.Contains(id) && _frame.All(contact => contact.Id != id))
            .Select(id => new RawContact(id, false, 0, 0))
            .ToList();
        output.AddRange(_frame);

        _live.Clear();
        _live.UnionWith(touching);
        _frame.Clear();
        _expected = 0;
        return output;
    }
}
