namespace PhoneDeck.Core;

/// <summary>
/// Notices a finger landing on the screen away from the phone's picture while input goes to the
/// phone, which returns input to this PC. Only a new touch counts: a finger that started on the
/// picture and slid off it is still the phone's. The button bar is not "outside".
/// </summary>
public sealed class OutsideTouchDetector
{
    private HashSet<int> _touching = [];

    /// <param name="frame">Contacts in screen pixels.</param>
    /// <param name="isOnPicture">Whether a point is on the phone's picture.</param>
    /// <param name="isOnBar">Whether a point is on PhoneDeck's button bar.</param>
    public bool Update(IReadOnlyList<RawContact> frame, Func<double, double, bool> isOnPicture,
        Func<double, double, bool> isOnBar)
    {
        var landedOutside = false;
        var touching = new HashSet<int>();
        foreach (var contact in frame)
        {
            if (!contact.Touching) continue;
            touching.Add(contact.Id);
            if (_touching.Contains(contact.Id)) continue;
            if (!isOnPicture(contact.X, contact.Y) && !isOnBar(contact.X, contact.Y)) landedOutside = true;
        }

        _touching = touching;
        return landedOutside;
    }
}
