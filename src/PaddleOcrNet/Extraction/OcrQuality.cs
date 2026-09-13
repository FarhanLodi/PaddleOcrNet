namespace PaddleOcrNet.Extraction;

/// <summary>
/// Summary confidence statistics for an OCR result, from
/// <see cref="OcrQualityExtensions.GetQuality(PaddleOcrNet.Models.OcrResult, double)"/>. Use it to route
/// documents: accept automatically, send to human review, or re-scan.
/// </summary>
public sealed record OcrQuality
{
    /// <summary>Number of recognized lines.</summary>
    public int LineCount { get; init; }

    /// <summary>Number of non-whitespace characters across all lines.</summary>
    public int CharacterCount { get; init; }

    /// <summary>Mean of the per-line confidences (0–1); 0 when there are no lines.</summary>
    public double MeanConfidence { get; init; }

    /// <summary>
    /// Mean confidence weighted by each line's character count, so long lines count more than short
    /// fragments (0–1); 0 when there are no lines.
    /// </summary>
    public double CharacterWeightedConfidence { get; init; }

    /// <summary>The lowest line confidence (0–1); 0 when there are no lines.</summary>
    public double MinConfidence { get; init; }

    /// <summary>Number of lines whose confidence is below <see cref="LowConfidenceThreshold"/>.</summary>
    public int LowConfidenceLineCount { get; init; }

    /// <summary><see cref="LowConfidenceLineCount"/> divided by <see cref="LineCount"/>; 0 when there are no lines.</summary>
    public double LowConfidenceRatio { get; init; }

    /// <summary>The threshold the low-confidence counts were computed with.</summary>
    public double LowConfidenceThreshold { get; init; }
}
