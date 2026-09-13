using System.Collections.Generic;

namespace PaddleOcrNet.Models;

/// <summary>
/// Represents a single word inside a recognized <see cref="OcrLine"/>, located from the recognizer's
/// per-character CTC timesteps (PaddleOCR 3.x's <c>return_word_box</c>). Populated only when
/// <see cref="RecognitionOptions.ReturnWordBoxes"/> is enabled. Words are split at spaces; every CJK
/// character (Han, Kana, Hangul) is its own word.
/// </summary>
public sealed record OcrWord
{
    /// <summary>
    /// Gets the recognized text of the word.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the confidence score (0-1 range): the mean probability of the word's characters.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Gets the coordinates of the word's bounding polygon, in the same image coordinates as the line's
    /// <see cref="OcrLine.BoundingPolygon"/> and following the line's slant.
    /// </summary>
    public IReadOnlyList<OcrPoint> BoundingPolygon { get; init; } = Array.Empty<OcrPoint>();

    /// <summary>
    /// Gets the axis-aligned bounding box computed from the polygon.
    /// </summary>
    public OcrBoundingBox BoundingBox { get; init; } = OcrBoundingBox.Empty;
}
