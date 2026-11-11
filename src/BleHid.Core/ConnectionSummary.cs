namespace BleHid.Core;

/// <summary>One line saying what is really connected, for people rather than diagnostics.</summary>
public static class ConnectionSummary
{
    public static string Describe(bool running, IReadOnlyList<string> hostNames)
    {
        if (!running) return "Not running";

        return hostNames.Count switch
        {
            0 => "No device connected",
            1 => $"Connected to {hostNames[0]}",
            _ => $"Connected to {hostNames.Count} devices"
        };
    }
}
