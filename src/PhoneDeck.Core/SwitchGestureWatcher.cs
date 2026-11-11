namespace PhoneDeck.Core;

/// <summary>
/// Watches the switch-gesture config file and reports changes, so toggling the gesture takes
/// effect on a session that is already running. The file is the single source of truth rather
/// than an in-process setting because background mode runs in a separate process: the UI and a
/// second CLI instance have no other way to reach it.
/// </summary>
public sealed class SwitchGestureWatcher : IDisposable
{
    private readonly string _path;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private SwitchGestureOptions _current;

    public SwitchGestureWatcher(string path)
    {
        _path = path;
        _current = Reload();

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return;

        try
        {
            Directory.CreateDirectory(directory);
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Without a watcher the settings simply stay as they were at startup, which is no
            // worse than having no live toggle at all.
            _watcher = null;
        }
    }

    public SwitchGestureOptions Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Raised on a watcher thread when the settings on disk actually differ.</summary>
    public event Action<SwitchGestureOptions>? Changed;

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        var reloaded = Reload();

        // Writers raise several notifications for one save. Comparing values rather than
        // debouncing on time means a change is reported exactly once however many arrive.
        lock (_gate)
        {
            if (_current.SameSettingsAs(reloaded)) return;
            _current = reloaded;
        }

        Changed?.Invoke(reloaded);
    }

    private SwitchGestureOptions Reload()
    {
        // Saving is not atomic, so a notification can arrive while the file is half written and
        // parse as garbage. A couple of quick retries turn that into the intended read instead
        // of a spurious "disabled".
        for (var attempt = 0; ; attempt++)
        {
            var options = SwitchGestureOptions.Load(_path);
            if (options.Warning is null || attempt == 3) return options;
            Thread.Sleep(30);
        }
    }

    public void Dispose()
    {
        if (_watcher is null) return;
        _watcher.Changed -= OnFileEvent;
        _watcher.Created -= OnFileEvent;
        _watcher.Deleted -= OnFileEvent;
        _watcher.Renamed -= OnFileEvent;
        _watcher.Dispose();
    }
}
