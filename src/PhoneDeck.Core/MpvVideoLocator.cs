using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PhoneDeck.Core;

/// <summary>
/// Keeps <see cref="VideoRegion"/> on the picture mpv is showing, wherever its window is and
/// whatever shape the video has. It finds mpv's window, and when mpv was started with
/// <c>--input-ipc-server=\\.\pipe\phonedeck-mpv</c> (PhoneDeck does this when it starts mpv) it
/// asks mpv for the exact black-bar margins. Without the pipe it assumes a 16:9 picture
/// letterboxed in the window. With no mpv window at all it falls back to a full-screen 16:9
/// picture, as before.
/// </summary>
public sealed class MpvVideoLocator : IDisposable
{
    public const string PipeName = "phonedeck-mpv";
    private const int PollMs = 400;
    private const int RequestId = 7;

    private readonly Func<int?> _preferredProcessId;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private string _lastSource = "";

    /// <param name="preferredProcessId">The mpv PhoneDeck started, if any; its window wins.</param>
    public MpvVideoLocator(Func<int?> preferredProcessId, Action<string> log)
    {
        _preferredProcessId = preferredProcessId;
        _log = log;
        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                VideoRect? found;
                try { found = await LocateAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Note($"viewer lookup failed: {ex.Message}");
                    ClosePipe();
                    found = null;
                }

                if (token.IsCancellationRequested) break;
                VideoRegion.Current = found;

                try { await Task.Delay(PollMs, token); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            // The pipe is only ever touched on this loop, so it is closed here, not in Dispose.
            ClosePipe();
        }
    }

    private async Task<VideoRect?> LocateAsync(CancellationToken token)
    {
        var window = FindMpvWindow(_preferredProcessId());
        if (window == IntPtr.Zero || IsIconic(window))
        {
            ClosePipe();
            Note("no mpv window; assuming a full-screen 16:9 picture");
            return null;
        }

        if (!GetClientRect(window, out var client)) return null;
        var origin = new POINT();
        if (!ClientToScreen(window, ref origin)) return null;

        var osd = await QueryOsdAsync(token);
        if (osd is { } dimensions)
        {
            Note("mpv window, margins from mpv");
            return FromOsd(dimensions, origin.X, origin.Y, client.Right, client.Bottom);
        }

        Note("mpv window without its control pipe; assuming 16:9 inside it");
        return VideoRect.Letterbox(origin.X, origin.Y, client.Right, client.Bottom, TouchMapper.DefaultVideoAspect);
    }

    /// <summary>One mpv command as a line for its JSON IPC.</summary>
    public static string CommandLine(params string[] command) =>
        JsonSerializer.Serialize(new { command }) + "\n";

    /// <summary>
    /// Sends one command to the mpv listening on <see cref="PipeName"/>, over a connection of its
    /// own (mpv serves several clients at once). Returns false if no such mpv is running.
    /// </summary>
    public static async Task<bool> SendCommandAsync(params string[] command)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(300);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(CommandLine(command)));
            await pipe.FlushAsync();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>mpv's osd-dimensions: window size and the black margins around the video.</summary>
    public readonly record struct OsdDimensions(double W, double H, double Left, double Top, double Right, double Bottom);

    public static OsdDimensions? ParseOsd(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("request_id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() != RequestId)
                return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

            double Get(string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble() : 0;

            var dimensions = new OsdDimensions(Get("w"), Get("h"), Get("ml"), Get("mt"), Get("mr"), Get("mb"));
            return dimensions.W > 0 && dimensions.H > 0 ? dimensions : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The video inside the window, scaled in case mpv's pixels differ from the window's.</summary>
    public static VideoRect FromOsd(OsdDimensions osd, double clientLeft, double clientTop, double clientWidth, double clientHeight)
    {
        var scaleX = clientWidth > 0 ? clientWidth / osd.W : 1;
        var scaleY = clientHeight > 0 ? clientHeight / osd.H : 1;
        var width = Math.Max(0, osd.W - osd.Left - osd.Right);
        var height = Math.Max(0, osd.H - osd.Top - osd.Bottom);
        return new VideoRect(clientLeft + osd.Left * scaleX, clientTop + osd.Top * scaleY, width * scaleX, height * scaleY);
    }

    private async Task<OsdDimensions?> QueryOsdAsync(CancellationToken token)
    {
        try
        {
            if (_pipe is not { IsConnected: true })
            {
                ClosePipe();
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(150, token); }
                catch (TimeoutException) { pipe.Dispose(); return null; }
                _pipe = pipe;
                _reader = new StreamReader(pipe, Encoding.UTF8);
            }

            var request = Encoding.UTF8.GetBytes(
                $"{{\"command\":[\"get_property\",\"osd-dimensions\"],\"request_id\":{RequestId}}}\n");
            await _pipe.WriteAsync(request, token);
            await _pipe.FlushAsync(token);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(300);
            // mpv also sends unrequested event lines; skip until the answer to this request.
            while (true)
            {
                var line = await _reader!.ReadLineAsync(timeout.Token);
                if (line is null) { ClosePipe(); return null; }
                if (!line.Contains("\"request_id\"")) continue;
                return ParseOsd(line);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            ClosePipe();
            return null;
        }
        catch (IOException)
        {
            ClosePipe();
            return null;
        }
    }

    private void ClosePipe()
    {
        _reader?.Dispose();
        _pipe?.Dispose();
        _reader = null;
        _pipe = null;
    }

    private void Note(string source)
    {
        if (source == _lastSource) return;
        _lastSource = source;
        _log(source);
    }

    /// <summary>The preferred process's mpv window, else the first visible mpv window.</summary>
    private static IntPtr FindMpvWindow(int? preferredProcessId)
    {
        IntPtr preferred = IntPtr.Zero, any = IntPtr.Zero;
        var className = new StringBuilder(16);

        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            className.Clear();
            if (GetClassName(window, className, className.Capacity) == 0 || className.ToString() != "mpv") return true;

            if (any == IntPtr.Zero) any = window;
            GetWindowThreadProcessId(window, out var processId);
            if (preferredProcessId is { } wanted && processId == wanted)
            {
                preferred = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return preferred != IntPtr.Zero ? preferred : any;
    }

    public void Dispose()
    {
        _stop.Cancel();
        VideoRegion.Current = null;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref POINT point);
}
