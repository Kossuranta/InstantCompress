namespace InstantCompress;

/// <summary>
/// Selectable compression preset.
/// </summary>
public enum Preset { Low, Medium, High }

/// <summary>
/// Encoder settings for a preset: JPEG quality, WebP quality (both 0-100), PNG zlib level (0-9),
/// and the longest-side cap in px applied when resize is enabled.
/// </summary>
public readonly record struct PresetSettings(int JpgQuality, int WebpQuality, int PngLevel, int MaxDim);

/// <summary>
/// Encoder settings for each <see cref="Preset"/>. <see cref="Preset.Low"/> means low compression
/// (highest quality, largest resize cap); <see cref="Preset.High"/> means high compression.
/// </summary>
public static class Presets
{
    /// <summary>
    /// The preset-to-settings lookup used by the UI and self-check.
    /// </summary>
    public static readonly Dictionary<Preset, PresetSettings> Values = new()
    {
        [Preset.Low] = new PresetSettings(90, 90, 9, 4096),
        [Preset.Medium] = new PresetSettings(75, 75, 8, 2048),
        [Preset.High] = new PresetSettings(60, 60, 6, 1280),
    };
}
