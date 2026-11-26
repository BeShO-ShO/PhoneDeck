using System.Diagnostics;
using PhoneDeck.Core;
using Windows.Media.Miracast;
using Windows.Media.Playback;

namespace PhoneDeck.App.Services;

/// <summary>
/// PhoneDeck's own cast receiver, in place of Windows' Wireless Display app. It listens the whole
/// time the Wireless route is chosen, as the app does while open, so the phone can cast before or
/// after capture starts. Each cast plays in a <see cref="MediaPlayer"/> shown in the viewer window.
/// <para>
/// It only reads Windows' "Projecting to this PC" settings (whether a PIN is needed, and so on) and
/// never changes them. The first cast makes Windows ask to let PhoneDeck through the firewall.
/// </para>
/// </summary>
public sealed class CastService
{
    public static CastService Instance { get; } = new();

    private static readonly TimeSpan EndingWait = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private Action<string> _log = _ => { };
    private MiracastReceiverSession? _session;
    private MediaPlayer? _player;
    private MiracastReceiverConnection? _connection;
    // Set while Stop waits for the phone to confirm that the cast it ended is over.
    private ManualResetEventSlim? _ending;
    private string? _phone;
    private int _generation;
    private bool _starting;

    private CastService()
    {
        AppSettings.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(AppSettings.ViewerSound)) return;
            lock (_gate)
            {
                if (_player is { } player) player.IsMuted = !AppSettings.Instance.ViewerSound;
            }
        };
    }

    public CastStatus Status { get; private set; } = new(CastState.Off);

    /// <summary>Raised on any thread.</summary>
    public event Action? StatusChanged;

    /// <summary>The cast now playing, for a viewer window that opens after it started.</summary>
    public MediaPlayer? CurrentPlayer
    {
        get { lock (_gate) return _player; }
    }

    /// <summary>Listens if the Wireless route is chosen, and stops otherwise.</summary>
    public void Apply(Action<string> log)
    {
        _log = log;
        // Started off the UI thread on purpose: a session made on the UI thread raises its events
        // there, so the cast's player was created on it, and the viewer window drawing that player
        // and the UI thread then waited on each other until both hung. On the thread pool the
        // session and its player stay on background threads, as they did in testing.
        if (AppSettings.Instance.DisplayRoute == DisplayRoute.Cast) _ = Task.Run(StartAsync);
        else Stop();
    }

    private async Task StartAsync()
    {
        int generation;
        lock (_gate)
        {
            if (_session is not null || _starting) return;
            _starting = true;
            generation = ++_generation;
        }

        SetStatus(new CastStatus(CastState.Starting));
        try
        {
            var receiver = new MiracastReceiver();
            var status = await receiver.GetStatusAsync();
            if (status.WiFiStatus == MiracastReceiverWiFiStatus.MiracastNotSupported)
            {
                _log("cast: this PC's Wi-Fi can't receive a cast (no Wi-Fi Direct)");
                SetStatus(new CastStatus(CastState.Unsupported));
                return;
            }

            var session = await receiver.CreateSessionAsync(null);
            session.ConnectionCreated += OnConnectionCreated;
            session.MediaSourceCreated += OnMediaSourceCreated;
            session.Disconnected += OnDisconnected;

            var result = await session.StartAsync();
            if (result.Status != MiracastReceiverSessionStartStatus.Success)
            {
                session.Dispose();
                _log($"cast: could not start receiving: {result.Status} {result.ExtendedError?.Message}");
                SetStatus(new CastStatus(CastState.Failed, Problem: result.Status.ToString()));
                return;
            }

            lock (_gate)
            {
                // Stopped (or the route changed) while starting.
                if (generation != _generation)
                {
                    session.Dispose();
                    return;
                }
                _session = session;
            }

            _log("cast: ready for the phone");
            // The app listens too while it is open, and the phone may pick it instead.
            if (Process.GetProcessesByName("Receiver").Length > 0)
                _log("cast: the Wireless Display app is also open; close it so the phone casts to PhoneDeck");
            SetStatus(new CastStatus(CastState.Waiting));
        }
        catch (Exception ex)
        {
            _log($"cast: could not start receiving: {ex.Message}");
            SetStatus(new CastStatus(CastState.Failed, Problem: ex.Message));
        }
        finally
        {
            lock (_gate) _starting = false;
        }
    }

    private void OnConnectionCreated(MiracastReceiverSession sender, MiracastReceiverConnectionCreatedEventArgs args)
    {
        var name = args.Connection.Transmitter.Name;
        lock (_gate)
        {
            _phone = name;
            _connection = args.Connection;
        }
        _log($"cast: {name} connected");
    }

    private void OnMediaSourceCreated(MiracastReceiverSession sender, MiracastReceiverMediaSourceCreatedEventArgs args)
    {
        var player = new MediaPlayer
        {
            RealTimePlayback = true,
            IsMuted = !AppSettings.Instance.ViewerSound,
            Source = args.MediaSource,
        };
        player.MediaOpened += (opened, _) =>
        {
            var session = opened.PlaybackSession;
            _log($"cast: picture {session.NaturalVideoWidth}x{session.NaturalVideoHeight}");
            OnUiThread(() => ViewerService.Instance.SetCastVideoSize((int)session.NaturalVideoWidth, (int)session.NaturalVideoHeight));
        };
        player.MediaFailed += (_, failed) => _log($"cast: playback failed: {failed.Error} {failed.ErrorMessage}");

        MediaPlayer? old;
        string? phone;
        lock (_gate)
        {
            old = _player;
            _player = player;
            phone = _phone;
        }

        // The old player is closed by the viewer once it has stopped drawing it; closing it here
        // while the window still used it hung the window when the phone left DeX.
        OnUiThread(() => ViewerService.Instance.ShowCast(player, _log, retired: old));
        // The phone waits for the receiver to start playing, and gives up after about half a minute.
        player.Play();
        SetStatus(new CastStatus(CastState.Connected, phone));
    }

    private void OnDisconnected(MiracastReceiverSession sender, MiracastReceiverDisconnectedEventArgs args)
    {
        MediaPlayer? old;
        lock (_gate)
        {
            _ending?.Set();
            old = _player;
            _player = null;
            _phone = null;
            _connection = null;
        }
        _log("cast: the phone disconnected");
        OnUiThread(() => ViewerService.Instance.ShowCast(null, _log, retired: old));
        lock (_gate)
        {
            if (_session is null) return;
        }
        SetStatus(new CastStatus(CastState.Waiting));
    }

    /// <summary>Stops listening and ends any cast.</summary>
    public void Stop()
    {
        MiracastReceiverSession? session;
        MediaPlayer? player;
        MiracastReceiverConnection? connection;
        lock (_gate)
        {
            _generation++;
            session = _session;
            _session = null;
            player = _player;
            _player = null;
            connection = _connection;
            _connection = null;
            _phone = null;
        }

        if (session is null && player is null && Status.State == CastState.Off) return;
        if (session is not null)
        {
            session.ConnectionCreated -= OnConnectionCreated;
            session.MediaSourceCreated -= OnMediaSourceCreated;
        }
        // Ends the cast on the phone as its own Disconnect would. Just dropping the session looks
        // to the phone like a lost link, so it cast again at once, and with PhoneDeck gone Windows
        // opened its Wireless Display app to take it.
        if (connection is not null)
        {
            using var ending = new ManualResetEventSlim(false);
            lock (_gate) _ending = ending;
            try
            {
                connection.Disconnect(MiracastReceiverDisconnectReason.DisconnectedByUser);
                _log("cast: ended the cast on the phone");
                // The session stays open a moment so the phone gets the message. On a slow PC (the
                // Surface Go 3) closing straight away left the phone casting again, and Windows
                // opened its Wireless Display app; waiting fixed it (tested 26 Nov 2026). Windows
                // usually raises no Disconnected for an end the receiver asked for, so the full
                // wait is normal. The receiver's events come on its own threads, so waiting here
                // can't hang them.
                _log(ending.Wait(EndingWait)
                    ? "cast: the phone confirmed the end"
                    : $"cast: gave the phone {EndingWait.TotalSeconds:0} s to end the cast");
            }
            catch (Exception ex) { _log($"cast: ending the cast: {ex.Message}"); }
            finally
            {
                lock (_gate) _ending = null;
            }
        }
        if (session is not null) session.Disconnected -= OnDisconnected;
        player?.Dispose();
        try { session?.Dispose(); }
        catch (Exception ex) { _log($"cast: stopping: {ex.Message}"); }
        if (session is not null) _log("cast: stopped receiving");
        SetStatus(new CastStatus(CastState.Off));
    }

    private void SetStatus(CastStatus status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    /// <summary>Never runs inline: the receiver's threads must not wait on the UI.</summary>
    private static void OnUiThread(Action action) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
}
