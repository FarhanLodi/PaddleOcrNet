using PaddleOcrNet.Models;

namespace PaddleOcrNet.Extraction;

/// <summary>
/// A value found in an <see cref="OcrResult"/> by
/// <see cref="OcrMatchExtensions.FindMatches(OcrResult, OcrPattern)"/>, with its location on the page.
/// </summary>
public sealed record OcrMatch
{
    /// <summary>
    /// The matched text exactly as recognized (never altered, even when a validator repaired an OCR
    /// confusion such as <c>O</c> for <c>0</c> to accept it).
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// A canonical form of the value when one is well defined, otherwise <c>null</c>: the compact upper-case
    /// IBAN, the card number digits, an ISO <c>yyyy-MM-dd</c> date (only when the day/month order is
    /// unambiguous), <c>"EUR 1234.56"</c> for amounts, <c>+15551234567</c> for phones, and so on.
    /// </summary>
    public string? Normalized { get; init; }

    /// <summary>The kind of value matched.</summary>
    public OcrMatchKind Kind { get; init; }

    /// <summary>The OCR line the value was found in.</summary>
    public required OcrLine Line { get; init; }

    /// <summary>Zero-based index of <see cref="Line"/> in <see cref="OcrResult.Lines"/>.</summary>
    public int LineIndex { get; init; }

    /// <summary>Zero-based character offset of the match within <see cref="OcrLine.Text"/>.</summary>
    public int Index { get; init; }

    /// <summary>Length of the match in characters (equals <c>Value.Length</c>).</summary>
    public int Length { get; init; }

    /// <summary>The recognition confidence (0–1) of the containing line.</summary>
    public double Confidence { get; init; }

    /// <summary>
    /// The estimated box of the matched characters: the line's box narrowed horizontally in proportion to
    /// the matched range's share of the line's display width (full-width characters count double) — the
    /// same estimate the hOCR/ALTO/TSV exporters use for word boxes, since the models only yield line geometry.
    /// </summary>
    public OcrBoundingBox BoundingBox { get; init; }
}
