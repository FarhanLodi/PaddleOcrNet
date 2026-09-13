namespace PaddleOcrNet.Extraction;

/// <summary>
/// Options for <see cref="LayoutTextExtensions.ToLayoutText(PaddleOcrNet.Models.OcrResult, LayoutTextOptions?)"/>.
/// </summary>
public sealed record LayoutTextOptions
{
    /// <summary>
    /// Maximum number of blank lines inserted for a vertical gap between rows (one blank line per full
    /// median line height of empty space). Default 1; 0 never inserts blank lines.
    /// </summary>
    public int MaxBlankLines { get; init; } = 1;

    /// <summary>
    /// Width in pixels of one character cell. <c>null</c> (the default) estimates it as the median of each
    /// line's width divided by its display width.
    /// </summary>
    public double? CharacterWidth { get; init; }

    /// <summary>
    /// Fraction (0–1] of the smaller line height two lines must overlap vertically to share a row. Default
    /// 0.5, which tolerates slight skew.
    /// </summary>
    public double RowOverlapThreshold { get; init; } = 0.5;

    /// <summary>
    /// Anchor predominantly right-to-left (Arabic, Hebrew) lines at the right edge of their box, so RTL
    /// paragraphs stay right-aligned. Default true.
    /// </summary>
    public bool RightAlignRtlLines { get; init; } = true;

    /// <summary>The default options.</summary>
    public static LayoutTextOptions Default { get; } = new();

    internal void Validate()
    {
        if (MaxBlankLines < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBlankLines), MaxBlankLines, "MaxBlankLines must not be negative.");
        if (CharacterWidth is { } width && (!double.IsFinite(width) || width <= 0))
            throw new ArgumentOutOfRangeException(nameof(CharacterWidth), width, "CharacterWidth must be a positive number.");
        if (double.IsNaN(RowOverlapThreshold) || RowOverlapThreshold <= 0 || RowOverlapThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(RowOverlapThreshold), RowOverlapThreshold, "RowOverlapThreshold must be in (0, 1].");
    }
}
