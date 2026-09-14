using System.Collections.Generic;

namespace PaddleOcrNet.Models;

/// <summary>
/// Represents a single line recognized in the OCR result.
/// </summary>
public sealed record OcrLine
{
    /// <summary>
    /// Gets the recognized text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the confidence score (0-1 range).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Gets the coordinates of the bounding polygon associated with the text line.
    /// </summary>
    public IReadOnlyList<OcrPoint> BoundingPolygon { get; init; } = Array.Empty<OcrPoint>();

    /// <summary>
    /// Gets the axis-aligned bounding box computed from the polygon.
    /// </summary>
    public OcrBoundingBox BoundingBox { get; init; } = OcrBoundingBox.Empty;

    /// <summary>
    /// Gets the word-level boxes of the line, in recognition (reading) order. Populated only when
    /// <see cref="RecognitionOptions.ReturnWordBoxes"/> is enabled, otherwise empty. Lines merged by
    /// <see cref="TextGrouping.Line"/> or <see cref="TextGrouping.Paragraph"/> grouping carry their members'
    /// words concatenated in the merged order.
    /// </summary>
    public IReadOnlyList<OcrWord> Words { get; init; } = Array.Empty<OcrWord>();
}
