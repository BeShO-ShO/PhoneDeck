namespace PhoneDeck.Core;

/// <summary>
/// Keeps the app's log on disk as well as on the Details page, so what happened just before
/// PhoneDeck exited can still be read afterwards. Each start begins a new file and keeps the
/// one before it as <c>.previous</c>. Writing never throws: losing a line is better than a crash.
/// </summary>
public sealed class SessionLog : IDisposable
{
    /// <summary>A file this size stops growing, so a runaway log can't fill the disk.</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    private readonly object _gate = new();
    private StreamWriter? _writer;

    public string Path { get; }

    public SessionLog(string path)
    {
        Path = path;
        try
        {
            var previous = PreviousPath(path);
            if (File.Exists(path)) File.Move(path, previous, overwrite: true);
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writer = null;
        }
    }

    public static string PreviousPath(string path) =>
        System.IO.Path.ChangeExtension(path, null) + ".previous" + System.IO.Path.GetExtension(path);

    public void Write(string line)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                if (_writer.BaseStream.Length >= MaxBytes) return;
                _writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _writer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); }
            catch (IOException) { }
            _writer = null;
        }
    }
}
