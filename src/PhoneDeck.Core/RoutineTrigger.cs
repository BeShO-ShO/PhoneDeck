namespace PhoneDeck.Core;

/// <summary>
/// Samsung's Modes and Routines only notice a Bluetooth device connecting through its classic
/// profiles (Audio, Calls), never through PhoneDeck's LE keyboard and mouse. So when a phone
/// connects, PhoneDeck can open the phone's Audio connection for a moment, which starts a routine
/// such as "start DeX and cast". Tested on a Galaxy S26 Ultra on 27 Nov 2026.
/// </summary>
public sealed class RoutineTrigger
{
    /// <summary>How long the audio connection is held open.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);

    /// <summary>A phone is not triggered again this soon, so a quick reconnect doesn't restart the routine.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(60);

    /// <summary>After choosing Wireless, how long Bluetooth stays off so the phone's routine ends.</summary>
    public static readonly TimeSpan DisconnectPause = TimeSpan.FromSeconds(2);

    /// <summary>How long after the phone connects its Audio is first opened, so the phone has settled.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    /// <summary>Opening the phone's Audio sometimes fails just after it reconnects; it is tried this often.</summary>
    public const int OpenAttempts = 3;

    /// <summary>The pause between tries at opening the phone's Audio.</summary>
    public static readonly TimeSpan OpenRetryGap = TimeSpan.FromSeconds(2);

    /// <summary>After Bluetooth restarts, how long the phone gets to reconnect by itself.</summary>
    public static readonly TimeSpan ReconnectWait = TimeSpan.FromSeconds(8);

    private readonly HashSet<string> _connected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastFired = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Takes the hosts connected now and returns those that just connected and should be triggered.
    /// </summary>
    /// <param name="hostIds">Device ids of the hosts connected now.</param>
    /// <param name="now">Environment.TickCount64.</param>
    public IReadOnlyList<string> Update(IEnumerable<string> hostIds, long now)
    {
        var current = new HashSet<string>(hostIds, StringComparer.OrdinalIgnoreCase);
        var fire = new List<string>();
        foreach (var id in current)
        {
            if (_connected.Contains(id)) continue;
            if (_lastFired.TryGetValue(id, out var last) && now - last < (long)MinGap.TotalMilliseconds) continue;
            _lastFired[id] = now;
            fire.Add(id);
        }
        _connected.Clear();
        _connected.UnionWith(current);
        return fire;
    }

    /// <summary>Records a trigger fired another way, so a connect straight after it doesn't fire again.</summary>
    public void NoteFired(string hostId, long now) => _lastFired[hostId] = now;

    /// <summary>Lets the phone's next connect fire however recently it last did (you chose Wireless).</summary>
    public void Forget(string hostId) => _lastFired.Remove(hostId);

    /// <summary>Forgets who is connected, so everyone connected at the next update counts as new.</summary>
    public void Reset() => _connected.Clear();

    /// <summary>
    /// Picks the phone's audio device for a host: the one whose id holds the host's address, or
    /// else the only one with the host's name. Null when none fits.
    /// </summary>
    /// <param name="address">The host's address as PhoneDeck shows it, such as "aa:bb:cc:dd:ee:ff".</param>
    /// <param name="name">The host's Bluetooth name, if known.</param>
    /// <param name="audioDevices">Windows' audio devices as (id, name).</param>
    public static string? FindAudioDevice(string address, string? name,
        IReadOnlyList<(string Id, string Name)> audioDevices)
    {
        var bare = address.Replace(":", "").Replace("-", "");
        if (bare.Length == 12)
        {
            var byAddress = audioDevices.FirstOrDefault(d =>
                d.Id.Contains(bare, StringComparison.OrdinalIgnoreCase));
            if (byAddress.Id is not null) return byAddress.Id;
        }

        if (string.IsNullOrWhiteSpace(name)) return null;
        var byName = audioDevices.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0].Id : null;
    }
}
