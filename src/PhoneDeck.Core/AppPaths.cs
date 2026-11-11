namespace PhoneDeck.Core;

/// <summary>
/// One home for everything the CLI and the app write, so the two never disagree about where
/// a user's logs are.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = PrepareRoot();

    private static string PrepareRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(localAppData, "PhoneDeck");
        MigrateSettings(Path.Combine(localAppData, "BleHid"), root);
        return root;
    }

    /// <summary>
    /// PhoneDeck was called BleHid. Its settings (remembered phone, switches, pacing) are copied
    /// across once, the first time the new folder is needed. Logs are left behind.
    /// </summary>
    internal static void MigrateSettings(string oldRoot, string newRoot)
    {
        if (Directory.Exists(newRoot) || !Directory.Exists(oldRoot)) return;
        try
        {
            Directory.CreateDirectory(newRoot);
            foreach (var file in Directory.GetFiles(oldRoot, "*.json"))
                File.Copy(file, Path.Combine(newRoot, Path.GetFileName(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting with defaults is better than failing to start.
        }
    }

    public static string Logs { get; } = Path.Combine(Root, "logs");

    /// <summary>Returns the full path, having created the directory it lives in.</summary>
    public static string InLogs(string fileName)
    {
        Directory.CreateDirectory(Logs);
        return Path.Combine(Logs, fileName);
    }

    public static string InRoot(string fileName)
    {
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, fileName);
    }

    /// <summary>Path of the pointer switch gesture configuration file.</summary>
    public static string SwitchGestureConfig => InRoot("switch-gesture.json");
}
