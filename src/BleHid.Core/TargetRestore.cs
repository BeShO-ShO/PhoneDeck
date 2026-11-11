namespace BleHid.Core;

/// <summary>Decides whether a remembered host may be selected again after a restart.</summary>
public static class TargetRestore
{
    /// <returns>The index of the host to select, or -1 to leave the target alone.</returns>
    /// <param name="alreadyDecided">The target was already restored or chosen during this run.</param>
    /// <param name="capturing">
    /// Restoring during capture would silently take the keyboard and mouse away from this PC.
    /// </param>
    public static int FindIndex(bool alreadyDecided, bool capturing, bool isLocalTarget,
        string? savedHostId, IReadOnlyList<string> hostIds)
    {
        if (alreadyDecided || capturing || !isLocalTarget || savedHostId is null) return -1;

        for (var i = 0; i < hostIds.Count; i++)
            if (string.Equals(hostIds[i], savedHostId, StringComparison.OrdinalIgnoreCase)) return i;

        return -1;
    }
}
