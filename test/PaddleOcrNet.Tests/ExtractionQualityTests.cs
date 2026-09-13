using PaddleOcrNet.Extraction;
using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-free tests for <see cref="OcrQualityExtensions"/>.
/// </summary>
public class ExtractionQualityTests
{
    private static OcrLine Line(string text, double confidence) => new() { Text = text, Confidence = confidence };

    private static OcrResult Result(params OcrLine[] lines) => new()
    {
        FullText = string.Join('\n', lines.Select(l => l.Text)),
        Lines = lines,
        Languages = new[] { "en" },
    };

    [Fact]
    public void GetQuality_computes_confidence_statistics()
    {
        var result = Result(Line("Hello", 0.9), Line("Hi there", 0.5), Line("abc", 0.85));

        var quality = result.GetQuality();

        Assert.Equal(3, quality.LineCount);
        Assert.Equal(15, quality.CharacterCount);
        Assert.Equal(0.75, quality.MeanConfidence, 6);
        Assert.Equal(10.55 / 15, quality.CharacterWeightedConfidence, 6);
        Assert.Equal(0.5, quality.MinConfidence, 6);
        Assert.Equal(1, quality.LowConfidenceLineCount);
        Assert.Equal(1.0 / 3, quality.LowConfidenceRatio, 6);
        Assert.Equal(0.8, quality.LowConfidenceThreshold);
    }

    [Fact]
    public void GetQuality_honors_custom_threshold()
    {
        var quality = Result(Line("Hello", 0.9), Line("Hi there", 0.5), Line("abc", 0.85)).GetQuality(0.95);
        Assert.Equal(3, quality.LowConfidenceLineCount);
        Assert.Equal(1.0, quality.LowConfidenceRatio, 6);
    }

    [Fact]
    public void GetQuality_of_empty_result_is_all_zero()
    {
        var quality = OcrResult.Empty.GetQuality();
        Assert.Equal(0, quality.LineCount);
        Assert.Equal(0, quality.MeanConfidence);
        Assert.Equal(0, quality.MinConfidence);
        Assert.Equal(0, quality.LowConfidenceRatio);
    }

    [Fact]
    public void GetLinesForReview_returns_low_confidence_lines_in_order()
    {
        var low1 = Line("smudged", 0.4);
        var low2 = Line("faint", 0.79);
        var result = Result(Line("clear", 0.99), low1, Line("fine", 0.8), low2);

        Assert.Equal(new[] { low1, low2 }, result.GetLinesForReview());
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Thresholds_outside_zero_to_one_throw(double threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrResult.Empty.GetQuality(threshold));
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrResult.Empty.GetLinesForReview(threshold));
    }
}
