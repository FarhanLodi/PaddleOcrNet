using PaddleOcrNet.Internal.Geometry;
using PaddleOcrNet.Internal.Recognition;
using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-free tests for the <see cref="RecognitionOptions.RetryBelowConfidence"/> rules: the grown
/// alternate region, the acceptance gate (confidence gain and edit-distance bound) and the edit distance.
/// </summary>
public class RecognitionRetryTests
{
    [Fact]
    public void Retry_is_off_by_default() => Assert.Equal(0, RecognitionOptions.Default.RetryBelowConfidence);

    [Fact]
    public void Horizontal_line_grows_by_three_tenths_height_at_the_ends_and_fifteen_hundredths_at_the_sides()
    {
        var geometry = new CropGeometry(
            new OcrPoint(10, 10), new OcrPoint(110, 10), new OcrPoint(110, 30), new OcrPoint(10, 30), 100, 20, false);

        var grown = RecognitionRetry.GrowQuad(geometry);

        Assert.Equal(new[] { P(4, 7), P(116, 7), P(116, 33), P(4, 33) }, grown.Select(Round));
    }

    [Fact]
    public void Vertical_line_grows_along_its_column()
    {
        // A 20-wide, 100-tall upright crop that was rotated for recognition: the text runs down the column,
        // so the line height is the width (20).
        var geometry = new CropGeometry(
            new OcrPoint(10, 10), new OcrPoint(30, 10), new OcrPoint(30, 110), new OcrPoint(10, 110), 20, 100, true);

        var grown = RecognitionRetry.GrowQuad(geometry);

        Assert.Equal(new[] { P(7, 4), P(33, 4), P(33, 116), P(7, 116) }, grown.Select(Round));
    }

    [Fact]
    public void Slanted_line_grows_along_its_own_axes()
    {
        // A 100×20 line rotated so its baseline runs along (0.8, 0.6).
        var tl = new OcrPoint(0, 0);
        var tr = new OcrPoint(80, 60);
        var bl = new OcrPoint(-12, 16);
        var br = new OcrPoint(68, 76);
        var geometry = new CropGeometry(tl, tr, br, bl, 100, 20, false);

        var grown = RecognitionRetry.GrowQuad(geometry);

        // Ends move 6 along the baseline, sides 3 across it.
        Assert.Equal(P(0 - 6 * 0.8 - 3 * -0.6, 0 - 6 * 0.6 - 3 * 0.8), Round(grown[0]));
        Assert.Equal(P(68 + 6 * 0.8 + 3 * -0.6, 76 + 6 * 0.6 + 3 * 0.8), Round(grown[2]));
    }

    [Theory]
    // original, conf, alternate, conf, accepted?
    [InlineData("Invoice 1234", 0.70f, "Invoice 1284", 0.80f, true)]    // +0.10, one substitution
    [InlineData("Invoice 1234", 0.70f, "Invoice 1234", 0.74f, false)]   // gain below 0.05
    [InlineData("Invoice 1234", 0.70f, "Invoice 1234", 0.75f, true)]    // exactly +0.05
    [InlineData("abcdefgh", 0.60f, "abcxyzgh", 0.90f, false)]           // 3 edits > max(2, 2)
    [InlineData("abcdefghijklmnopqrst", 0.60f, "abcdeXXXXXklmnopqrst", 0.90f, true)]  // 5 edits = 25% of 20
    [InlineData("abcdefghijklmnopqrst", 0.60f, "abXXXXXXhijklmnopqrs", 0.90f, false)] // 7 edits
    [InlineData("ab", 0.50f, "   ", 0.99f, false)]                      // blank alternate
    public void Acceptance_requires_a_confidence_gain_and_a_small_edit(
        string original, float originalConfidence, string alternate, float alternateConfidence, bool expected)
    {
        Assert.Equal(expected, RecognitionRetry.Accept(
            new RecognizedText(original, originalConfidence), new RecognizedText(alternate, alternateConfidence)));
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("same", "same", 0)]
    [InlineData("flaw", "lawn", 2)]
    public void Levenshtein_distance(string a, string b, int expected)
        => Assert.Equal(expected, RecognitionRetry.Levenshtein(a, b));

    private static (double, double) P(double x, double y) => (Math.Round(x, 6), Math.Round(y, 6));

    private static (double, double) Round(OcrPoint p) => (Math.Round(p.X, 6), Math.Round(p.Y, 6));
}
