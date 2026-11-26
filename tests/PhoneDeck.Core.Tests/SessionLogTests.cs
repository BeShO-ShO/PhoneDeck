using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public sealed class SessionLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhoneDeckSessionLog-" + Guid.NewGuid().ToString("N"));

    public SessionLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string LogPath => Path.Combine(_dir, "PhoneDeck-session.log");

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void WritesLinesAsTheyCome()
    {
        using var log = new SessionLog(LogPath);
        log.Write("cast: ended the cast on the phone");
        log.Write("cast: stopped receiving");

        Assert.Equal(
            ["cast: ended the cast on the phone", "cast: stopped receiving"],
            ReadShared(LogPath).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void KeepsTheLastRunAsPrevious()
    {
        using (var first = new SessionLog(LogPath)) first.Write("first run");
        using (var second = new SessionLog(LogPath)) second.Write("second run");

        Assert.Equal(Path.Combine(_dir, "PhoneDeck-session.previous.log"), SessionLog.PreviousPath(LogPath));
        Assert.Contains("first run", ReadShared(SessionLog.PreviousPath(LogPath)));
        Assert.DoesNotContain("first run", ReadShared(LogPath));
        Assert.Contains("second run", ReadShared(LogPath));
    }

    [Fact]
    public void NeverThrowsWhenTheFolderIsMissing()
    {
        using var log = new SessionLog(Path.Combine(_dir, "missing", "PhoneDeck-session.log"));
        log.Write("lost, quietly");
    }

    [Fact]
    public void IgnoresWritesAfterDispose()
    {
        var log = new SessionLog(LogPath);
        log.Dispose();
        log.Write("too late");
        Assert.Equal("", ReadShared(LogPath));
    }
}
