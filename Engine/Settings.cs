using System.Text.Json;

namespace InstantCompress;

/// <summary>
/// Persisted user choices. Plain mutable POCO so <see cref="System.Text.Json"/> round-trips it without attributes.
/// </summary>
public sealed class Settings
{
    public string Preset { get; set; } = "medium";
    public string Format { get; set; } = "jpg";
    public bool CustomOn { get; set; }
    public int CustomJpg { get; set; } = 80; // 1-100
    public int CustomPng { get; set; } = 6;  // 0-9
    public bool ResizeOn { get; set; } = true;
    public string ResizeMode { get; set; } = "longest"; // "longest" / "widthheight" / "percentage" (Custom only)
    public int MaxDim { get; set; } = 2048;   // longest-side cap in px when ResizeOn
    public int MaxWidth { get; set; } = 1920; // width cap in px for ResizeMode "widthheight"
    public int MaxHeight { get; set; } = 1080; // height cap in px for ResizeMode "widthheight"
    public int ScalePercent { get; set; } = 50; // scale % for ResizeMode "percentage"
    public string? Theme { get; set; }        // "light" / "dark", or null to follow the OS theme
}

/// <summary>
/// Loads/saves <see cref="Settings"/> as a single JSON file in app-data. No config framework; failures are non-fatal.
/// </summary>
public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "InstantCompress", "settings.json");

    /// <summary>
    /// Reads settings, or returns defaults if the file is missing or unreadable.
    /// </summary>
    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt/unreadable: fall back to defaults */ }
        return new Settings();
    }

    /// <summary>
    /// Writes settings; swallows IO errors (persistence is a convenience, not correctness).
    /// </summary>
    public static void Save(Settings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
