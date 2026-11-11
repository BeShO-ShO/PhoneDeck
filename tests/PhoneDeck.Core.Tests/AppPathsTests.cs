using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Root_lives_under_local_application_data()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, AppPaths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PhoneDeck", Path.GetFileName(AppPaths.Root));
    }

    [Fact]
    public void Logs_is_a_subfolder_of_root()
    {
        Assert.Equal(AppPaths.Root, Path.GetDirectoryName(AppPaths.Logs));
        Assert.Equal("logs", Path.GetFileName(AppPaths.Logs));
    }

    [Fact]
    public void InLogs_creates_the_directory_and_returns_a_path_inside_it()
    {
        var path = AppPaths.InLogs("unit-test-probe.txt");

        Assert.True(Directory.Exists(AppPaths.Logs));
        Assert.Equal(AppPaths.Logs, Path.GetDirectoryName(path));
    }

    [Fact]
    public void InRoot_keeps_settings_out_of_the_logs_folder()
    {
        var path = AppPaths.InRoot("app-settings.json");

        Assert.Equal(AppPaths.Root, Path.GetDirectoryName(path));
        Assert.NotEqual(AppPaths.Logs, Path.GetDirectoryName(path));
    }

    [Fact]
    public void Settings_from_the_old_name_are_copied_once_and_logs_are_left()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"phonedeck-test-{Guid.NewGuid():N}");
        var oldRoot = Path.Combine(temp, "BleHid");
        var newRoot = Path.Combine(temp, "PhoneDeck");
        try
        {
            Directory.CreateDirectory(Path.Combine(oldRoot, "logs"));
            File.WriteAllText(Path.Combine(oldRoot, "app-settings.json"), "{\"x\":1}");
            File.WriteAllText(Path.Combine(oldRoot, "logs", "old.log"), "log");

            AppPaths.MigrateSettings(oldRoot, newRoot);

            Assert.Equal("{\"x\":1}", File.ReadAllText(Path.Combine(newRoot, "app-settings.json")));
            Assert.False(Directory.Exists(Path.Combine(newRoot, "logs")));

            // A second run must not overwrite settings changed since.
            File.WriteAllText(Path.Combine(newRoot, "app-settings.json"), "{\"x\":2}");
            AppPaths.MigrateSettings(oldRoot, newRoot);
            Assert.Equal("{\"x\":2}", File.ReadAllText(Path.Combine(newRoot, "app-settings.json")));
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Migration_does_nothing_without_an_old_folder()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"phonedeck-test-{Guid.NewGuid():N}");
        AppPaths.MigrateSettings(Path.Combine(temp, "BleHid"), Path.Combine(temp, "PhoneDeck"));

        Assert.False(Directory.Exists(Path.Combine(temp, "PhoneDeck")));
    }

    /// <summary>Every log a user is asked to send must sit in one folder they can zip.</summary>
    [Fact]
    public void All_known_log_files_resolve_into_the_logs_folder()
    {
        foreach (var name in new[] { "PhoneDeck.log", "PhoneDeck-app.log", "diagnostics.txt" })
            Assert.Equal(AppPaths.Logs, Path.GetDirectoryName(AppPaths.InLogs(name)));
    }
}
