namespace InstantCompress;

/// <summary>
/// Selectable compression preset.
/// </summary>
public enum Preset { Low, Medium, High }

/// <summary>
/// Encoder settings for a preset: JPEG quality (0-100) and PNG zlib level (0-9).
/// </summary>
public readonly record struct PresetSettings(int JpgQuality, int PngLevel);

/// <summary>
/// Encoder settings for each <see cref="Preset"/>.
/// </summary>
public static class Presets
{
    /// <summary>
    /// The preset-to-settings lookup used by the UI and self-check.
    /// </summary>
    public static readonly Dictionary<Preset, PresetSettings> Values = new()
    {
        [Preset.Low] = new PresetSettings(60, 6),
        [Preset.Medium] = new PresetSettings(75, 8),
        [Preset.High] = new PresetSettings(90, 9),
    };
}
