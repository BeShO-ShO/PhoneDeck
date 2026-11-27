using PhoneDeck.Core;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace PhoneDeck.App.Services;

/// <summary>
/// Opens a phone's Bluetooth Audio connection for a moment when it connects, so a routine on the
/// phone that waits for this PC's Bluetooth starts (see <see cref="RoutineTrigger"/>).
/// </summary>
public static class RoutineTriggerService
{
    public static void Fire(HostTarget host, Action<string> log) => _ = Task.Run(async () =>
    {
        var name = host.Name ?? host.Address;
        try
        {
            // Straight after the phone connects, the first try failed after about 6 s (27 Nov 2026).
            await Task.Delay(RoutineTrigger.SettleDelay);
            var found = await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector());
            var devices = found.Select(d => (d.Id, d.Name)).ToList();
            var id = RoutineTrigger.FindAudioDevice(host.Address, host.Name, devices);
            if (id is null)
            {
                log($"routine: {name} has no Audio connection to this PC; is it paired for Audio?");
                return;
            }

            // Just after the phone reconnects, opening its Audio failed once with UnknownFailure
            // and worked on the next go (27 Nov 2026).
            for (var attempt = 1; attempt <= RoutineTrigger.OpenAttempts; attempt++)
            {
                if (attempt > 1) await Task.Delay(RoutineTrigger.OpenRetryGap);
                using var connection = AudioPlaybackConnection.TryCreateFromId(id);
                if (connection is null)
                {
                    log($"routine: couldn't reach {name}'s Audio connection");
                    continue;
                }
                await connection.StartAsync();
                var result = await connection.OpenAsync();
                if (result.Status != AudioPlaybackConnectionOpenResultStatus.Success)
                {
                    log($"routine: {name} didn't open Audio ({result.Status}), try {attempt} of {RoutineTrigger.OpenAttempts}");
                    continue;
                }
                log($"routine: opened {name}'s Audio for {RoutineTrigger.Hold.TotalSeconds:0} s to start its routine");
                await Task.Delay(RoutineTrigger.Hold);
                return;
            }
        }
        catch (Exception ex)
        {
            log($"routine: {ex.Message}");
        }
    });
}
