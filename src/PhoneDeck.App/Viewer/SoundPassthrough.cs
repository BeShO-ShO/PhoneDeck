using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PhoneDeck.App.Viewer;

/// <summary>
/// Plays the capture device's sound on the default speakers. It runs apart from the picture,
/// so it never delays it, but sound behind the picture is a lag of its own: the queue is kept
/// short and emptied whenever it grows past <see cref="MaxBacklog"/>.
/// </summary>
internal sealed class SoundPassthrough : IDisposable
{
    private static readonly TimeSpan MaxBacklog = TimeSpan.FromMilliseconds(120);
    private const int CaptureBufferMs = 20;
    private const int OutputLatencyMs = 40;

    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _queue;
    private readonly WasapiOut _output;
    private readonly Action<string> _log;
    private int _disposed;

    private SoundPassthrough(MMDevice device, Action<string> log)
    {
        _device = device;
        _log = log;
        _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: CaptureBufferMs);
        // Half a second of room; the backlog is cut far sooner (see OnData).
        _queue = new BufferedWaveProvider(_capture.WaveFormat, TimeSpan.FromMilliseconds(500))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is { } ex && Volatile.Read(ref _disposed) == 0) _log($"viewer: sound stopped: {ex.Message}");
        };

        _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: OutputLatencyMs);
        _output.Init(_queue);
    }

    /// <summary>Starts the sound, or logs why it can't and returns null. The picture works either way.</summary>
    public static SoundPassthrough? TryStart(string deviceName, Action<string> log)
    {
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var inputs = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            device = inputs.FirstOrDefault(d => string.Equals(d.FriendlyName, deviceName, StringComparison.OrdinalIgnoreCase))
                     ?? inputs.FirstOrDefault(d => d.FriendlyName.Contains(deviceName, StringComparison.OrdinalIgnoreCase));
            foreach (var other in inputs.Where(d => !ReferenceEquals(d, device))) other.Dispose();

            if (device is null)
            {
                log($"viewer: sound device \"{deviceName}\" not found; no sound");
                return null;
            }

            var sound = new SoundPassthrough(device, log);
            sound._capture.StartRecording();
            sound._output.Play();
            log($"viewer: sound from \"{device.FriendlyName}\" ({sound._capture.WaveFormat})");
            return sound;
        }
        catch (Exception ex)
        {
            log($"viewer: no sound: {ex.Message}");
            device?.Dispose();
            return null;
        }
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        // Sound running behind the picture is worse than a tiny gap, so drop the backlog.
        if (_queue.BufferedDuration > MaxBacklog) _queue.ClearBuffer();
        _queue.AddSamples(args.Buffer, 0, args.BytesRecorded);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _capture.StopRecording(); } catch (Exception) { }
        try { _output.Stop(); } catch (Exception) { }
        _capture.Dispose();
        _output.Dispose();
        _device.Dispose();
    }
}
