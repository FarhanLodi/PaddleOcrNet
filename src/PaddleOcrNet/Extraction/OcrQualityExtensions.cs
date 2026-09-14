using PaddleOcrNet.Extraction.Internal;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Extraction;

/// <summary>
/// Confidence-based quality helpers for <see cref="OcrResult"/>: page-level statistics and the lines a
/// human should double-check.
/// </summary>
public static class OcrQualityExtensions
{
    /// <summary>
    /// Computes confidence statistics for the result.
    /// </summary>
    /// <param name="result">The OCR result.</param>
    /// <param name="lowConfidenceThreshold">Lines below this confidence (0–1) count as low-confidence. Default 0.8.</param>
    /// <returns>The statistics; all zero for a result with no lines.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lowConfidenceThreshold"/> is outside 0–1.</exception>
    public static OcrQuality GetQuality(this OcrResult result, double lowConfidenceThreshold = 0.8)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateThreshold(lowConfidenceThreshold, nameof(lowConfidenceThreshold));

        var lines = result.Lines;
        if (lines.Count == 0)
        {
            return new OcrQuality { LowConfidenceThreshold = lowConfidenceThreshold };
        }

        double sum = 0, min = double.MaxValue;
        int low = 0, characters = 0;
        foreach (var line in lines)
        {
            sum += line.Confidence;
            min = Math.Min(min, line.Confidence);
            if (line.Confidence < lowConfidenceThreshold) low++;
            characters += TextGeometry.CountCharacters(line.Text);
        }

        return new OcrQuality
        {
            LineCount = lines.Count,
            CharacterCount = characters,
            MeanConfidence = sum / lines.Count,
            CharacterWeightedConfidence = TextGeometry.WeightedConfidence(lines),
            MinConfidence = min,
            LowConfidenceLineCount = low,
            LowConfidenceRatio = (double)low / lines.Count,
            LowConfidenceThreshold = lowConfidenceThreshold,
        };
    }

    /// <summary>
    /// Returns the lines whose confidence is below <paramref name="threshold"/>, in their original
    /// reading order — the lines worth a human look.
    /// </summary>
    /// <param name="result">The OCR result.</param>
    /// <param name="threshold">Confidence (0–1) below which a line needs review. Default 0.8.</param>
    /// <returns>The low-confidence lines; empty when every line meets the threshold.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside 0–1.</exception>
    public static IReadOnlyList<OcrLine> GetLinesForReview(this OcrResult result, double threshold = 0.8)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateThreshold(threshold, nameof(threshold));
        return result.Lines.Where(l => l.Confidence < threshold).ToList();
    }

    private static void ValidateThreshold(double threshold, string paramName)
    {
        if (double.IsNaN(threshold) || threshold < 0 || threshold > 1)
        {
            throw new ArgumentOutOfRangeException(paramName, threshold, "The threshold must be between 0 and 1.");
        }
    }
}
