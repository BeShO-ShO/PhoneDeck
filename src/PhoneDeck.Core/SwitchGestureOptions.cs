using System.Text.Json;

namespace PhoneDeck.Core;

/// <summary>
/// Configuration for the pointer switch gesture: shaking the pointer back and forth cycles the
/// target between this PC and the paired hosts. Loaded from a JSON file next to the other
/// per-user settings, and disabled unless the file explicitly turns it on.
/// </summary>
public sealed class SwitchGestureOptions
{
    private const int DefaultMinStrokePx = 80;
    private const int DefaultRequiredReversals = 3;
    // A shake is a quick flick, not a slow wave. A generous window makes the gesture easy to
    // trigger by accident, because unrelated pointer movement has time to drift into the shape.
    private const int DefaultWindowMs = 250;

    private SwitchGestureOptions(bool enabled, int minStrokePx, int requiredReversals,
        int windowMs, string? warning)
    {
        var notes = new List<string>();
        if (warning is not null) notes.Add(warning);

        Enabled = enabled;
        MinStrokePx = Clamp(nameof(minStrokePx), minStrokePx, 20, 500, notes);
        RequiredReversals = Clamp(nameof(requiredReversals), requiredReversals, 2, 10, notes);
        WindowMs = Clamp(nameof(windowMs), windowMs, 100, 5000, notes);
        Warning = notes.Count > 0 ? string.Join("; ", notes) : null;
    }

    /// <summary>Master switch. When false the pointer behaves exactly as it did before.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// How far (pixels) the pointer must travel in one direction for that stroke to count
    /// towards the gesture. Shorter strokes are treated as jitter and break the chain.
    /// </summary>
    public int MinStrokePx { get; }

    /// <summary>
    /// How many direction reversals make a shake. Three reversals is four strokes, roughly two
    /// full back-and-forth sweeps.
    /// </summary>
    public int RequiredReversals { get; }

    /// <summary>How long (ms) the reversals have to arrive within to count as one gesture.</summary>
    public int WindowMs { get; }

    /// <summary>Set when the file could not be read, or a value had to be clamped.</summary>
    public string? Warning { get; }

    /// <summary>Test factory: builds options directly without a config file.</summary>
    internal static SwitchGestureOptions ForTest(bool enabled, int minStrokePx,
        int requiredReversals, int windowMs) =>
        new(enabled, minStrokePx, requiredReversals, windowMs, null);

    /// <summary>Returns a copy with the gesture turned on or off, keeping the tuning values.</summary>
    public SwitchGestureOptions WithEnabled(bool enabled) =>
        new(enabled, MinStrokePx, RequiredReversals, WindowMs, null);

    /// <summary>True when both describe the same settings, ignoring any load warning.</summary>
    internal bool SameSettingsAs(SwitchGestureOptions other) =>
        Enabled == other.Enabled &&
        MinStrokePx == other.MinStrokePx &&
        RequiredReversals == other.RequiredReversals &&
        WindowMs == other.WindowMs;

    /// <summary>
    /// Writes the settings back to disk. The file is the single source of truth, so a toggle
    /// from the UI reaches a background instance that is running in another process entirely.
    /// </summary>
    public bool TrySave(string path, out string? error)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(new FileModel
            {
                Enabled = Enabled,
                MinStrokePx = MinStrokePx,
                RequiredReversals = RequiredReversals,
                WindowMs = WindowMs
            }, new JsonSerializerOptions { WriteIndented = true }));
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static SwitchGestureOptions Load(string path)
    {
        if (!File.Exists(path)) return Disabled(null);

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Disabled($"could not read {path}: {ex.Message}");
        }
    }

    internal static SwitchGestureOptions Parse(string json)
    {
        var model = JsonSerializer.Deserialize<FileModel>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new FileModel();
        return new(model.Enabled ?? false,
            model.MinStrokePx ?? DefaultMinStrokePx,
            model.RequiredReversals ?? DefaultRequiredReversals,
            model.WindowMs ?? DefaultWindowMs,
            null);
    }

    private static SwitchGestureOptions Disabled(string? warning) =>
        new(false, DefaultMinStrokePx, DefaultRequiredReversals, DefaultWindowMs, warning);

    // An out-of-range value is clamped and reported rather than silently replaced by the
    // default, so a typo in the config does not look like the setting was ignored.
    private static int Clamp(string name, int value, int min, int max, List<string> notes)
    {
        if (value >= min && value <= max) return value;
        var clamped = Math.Clamp(value, min, max);
        notes.Add($"{name} {value} is outside {min}..{max}, using {clamped}");
        return clamped;
    }

    private sealed class FileModel
    {
        public bool? Enabled { get; init; }
        public int? MinStrokePx { get; init; }
        public int? RequiredReversals { get; init; }
        public int? WindowMs { get; init; }
    }
}
