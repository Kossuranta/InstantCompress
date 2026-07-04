namespace InstantCompress;

/// <summary>
/// How a resize's target dimensions are derived from the source image.
/// </summary>
public enum ResizeMode
{
    /// <summary>Scale so the longer of width/height equals <see cref="ResizeSettings.MaxDim"/>.</summary>
    LongestSide,

    /// <summary>
    /// Cap <see cref="ResizeSettings.MaxWidth"/> and/or <see cref="ResizeSettings.MaxHeight"/> (0 = uncapped);
    /// at least one must be set.
    /// </summary>
    Dimensions,

    /// <summary>Scale both dimensions by <see cref="ResizeSettings.Percent"/>.</summary>
    Percentage,
}

/// <summary>
/// Resize configuration for a batch: whether it's on, which <see cref="ResizeMode"/>, and that mode's values.
/// </summary>
public readonly record struct ResizeSettings(bool Enabled, ResizeMode Mode, int MaxDim, int MaxWidth, int MaxHeight, int Percent)
{
    /// <summary>
    /// No resize.
    /// </summary>
    public static readonly ResizeSettings Off = new(false, ResizeMode.LongestSide, 0, 0, 0, 100);
}
