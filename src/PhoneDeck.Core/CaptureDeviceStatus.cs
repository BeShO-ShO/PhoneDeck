namespace PhoneDeck.Core;

/// <summary>
/// Home's one-line answer to "is the capture device ready?": chosen and plugged in, chosen but
/// missing, or not chosen yet.
/// </summary>
public readonly record struct CaptureDeviceStatus(string Text, bool IsProblem)
{
    /// <param name="chosen">The video device chosen in Settings, or null.</param>
    /// <param name="found">The video devices Windows sees now.</param>
    public static CaptureDeviceStatus Describe(string? chosen, IReadOnlyList<string> found)
    {
        // None chosen is how a separate monitor is used (nothing opens), so it isn't a problem.
        if (string.IsNullOrWhiteSpace(chosen))
        {
            return found.Count == 0
                ? new("None chosen or found, so the phone's screen won't open here. Plug one in and choose it in Settings.", false)
                : new($"None chosen, so the phone's screen won't open here. Choose it in Settings (found: {string.Join(", ", found)}).", false);
        }

        return found.Contains(chosen, StringComparer.OrdinalIgnoreCase)
            ? new($"{chosen} is plugged in.", false)
            : new($"{chosen} isn't plugged in.", true);
    }
}
