using PaddleOcrNet.Models;

namespace PaddleOcrNet.Extraction;

/// <summary>
/// The text read from one <see cref="OcrZone"/>.
/// </summary>
public sealed record OcrZoneResult
{
    /// <summary>The zone's name.</summary>
    public required string Name { get; init; }

    /// <summary>The zone definition that was read.</summary>
    public required OcrZone Zone { get; init; }

    /// <summary>
    /// The zone's text in reading order: rows separated by <c>\n</c>, words within a row by a space
    /// (<see cref="ZoneMode.SingleLine"/> zones are joined into one line). Empty when nothing was read.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>Character-weighted mean confidence (0–1) of the zone's lines; 0 when nothing was read.</summary>
    public double Confidence { get; init; }

    /// <summary>The recognized lines, with boxes in the full image's coordinates.</summary>
    public required IReadOnlyList<OcrLine> Lines { get; init; }

    /// <summary>The underlying OCR result for the zone.</summary>
    public required OcrResult Result { get; init; }
}
