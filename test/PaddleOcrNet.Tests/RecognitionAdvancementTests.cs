using System.Globalization;
using PaddleOcrNet.Internal.Recognition;
using Xunit;

namespace PaddleOcrNet.Tests;

public class RecognitionAdvancementTests
{
    [Theory]
    [InlineData("第", "一")]
    [InlineData("a", "b")]
    public void StitchingPreservesOriginalSubpixelWordGeometry(string first, string second)
    {
        var original = new RecognizedText(first + second, .9f)
        {
            Characters = [new(first, .9f, 1, 1), new(second, .9f, 10, 10)],
            StepWidth = 4.5,
        };
        var expected = WordBoxBuilder.SplitWords(original.Characters!, original.StepWidth, 100);
        var stitched = LongLineSegmentation.Stitch([new(0, 100)], [original], 100, true);
        var actual = WordBoxBuilder.SplitWords(stitched.Characters!, stitched.StepWidth, 100);
        Assert.Equal(expected, actual);
        Assert.All(stitched.Characters!, c => Assert.NotNull(c.PixelGeometry));
        Assert.Equal(6.75, stitched.Characters![0].PixelGeometry!.Value.Center);
    }

    [Fact]
    public void StitchedCjkCentersRetainSourceOffsetAcrossWindows()
    {
        var reading = new RecognizedText("字", .9f)
        { Characters = [new("字", .9f, 10, 10)], StepWidth = 4.5 };
        var stitched = LongLineSegmentation.Stitch([new(0, 80), new(40, 80)], [reading, reading], 120, true);
        Assert.Equal(47.25, stitched.Characters![0].PixelGeometry!.Value.Center);
        Assert.Equal(87.25, stitched.Characters![1].PixelGeometry!.Value.Center);
        var words = WordBoxBuilder.SplitWords(stitched.Characters!, 1, 120);
        Assert.Equal(47.25, (words[0].Start + words[0].End) / 2);
        Assert.Equal(87.25, (words[1].Start + words[1].End) / 2);
    }

    [Fact]
    public void PixelBudgetAccountsForPaddingAndPreservesAllIndices()
    {
        double[] ratios = [2, 3, 8, 9];
        var batches = RecognitionBatchPlanner.Plan(ratios, 6, 48, 48 * 640);
        Assert.Equal(new[] { (0, 2), (2, 3), (3, 4) }, batches);
    }

    [Fact]
    public void TooSmallPixelBudgetFailsBeforeTensorAllocation()
        => Assert.Throws<ArgumentException>(() => RecognitionBatchPlanner.Plan([70], 6, 48, 48 * 3199));

    [Fact]
    public void ZeroBudgetKeepsCountOnlyBatches()
        => Assert.Equal(new[] { (0, 3), (3, 5) }, RecognitionBatchPlanner.Plan([1, 2, 3, 4, 80], 3, 48, 0));

    [Fact]
    public void WindowsCoverEntireLineWithinModelWidth()
    {
        var windows = LongLineSegmentation.Plan(15000, 48, 48, 0.2, 64);
        Assert.Equal(0, windows[0].Start);
        Assert.Equal(15000, windows[^1].Start + windows[^1].Width);
        for (int i = 1; i < windows.Count; i++)
        {
            Assert.InRange(windows[i].Width, 1, 3200);
            Assert.True(windows[i].Start < windows[i - 1].Start + windows[i - 1].Width);
        }
    }

    [Fact]
    public void SegmentLimitRejectsUnboundedExpansion()
        => Assert.Throws<ArgumentException>(() => LongLineSegmentation.Plan(int.MaxValue, 48, 48, .2, 64));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(.5)]
    public void InvalidOverlapRejected(double overlap)
        => Assert.Throws<ArgumentOutOfRangeException>(() => LongLineSegmentation.Plan(4000, 48, 48, overlap, 64));

    [Theory]
    [InlineData("go go go go go go")]
    [InlineData("8:47 8:47 $1,280!")]
    [InlineData("e\u0301 e\u0301 e\u0301 e\u0301 e\u0301 e\u0301")]
    [InlineData("مرحبا مرحبا مرحبا")]
    [InlineData("😀 😀 😀 😀 😀 😀 😀")]
    public void OverlapPreservesRepeatedPunctuationUnicodeAndEmissionOrder(string source)
    {
        var elements = StringInfo.GetTextElementEnumerator(source);
        var tokens = new List<string>();
        while (elements.MoveNext()) tokens.Add(elements.GetTextElement());
        int width = tokens.Count * 10;
        int middle = tokens.Count / 2;
        var windows = new[]
        {
            new LongLineSegmentation.Window(0, (middle + 2) * 10),
            new LongLineSegmentation.Window((middle - 2) * 10, width - (middle - 2) * 10),
        };
        var readings = windows.Select(w =>
        {
            var chars = tokens.Skip(w.Start / 10).Take(w.Width / 10)
                .Select((t, i) => new CtcCharacter(t, .9f, i * 10, i * 10 + 9)).ToArray();
            return new RecognizedText(string.Concat(chars.Select(c => c.Token)), .9f) { Characters = chars, StepWidth = 1 };
        }).ToArray();
        var result = LongLineSegmentation.Stitch(windows, readings, width, true);
        Assert.Equal(source, result.Text);
        Assert.Equal(tokens.Count, result.Characters!.Count);
        Assert.Equal(1, result.StepWidth);
        for (int i = 0; i < result.Characters.Count; i++)
        {
            Assert.Equal(i * 10, result.Characters[i].FirstStep);
            Assert.Equal(i * 10 + 9, result.Characters[i].LastStep);
        }
    }

    [Fact]
    public void CombiningTokensCrossingSeamAreOwnedTogether()
    {
        var windows = new[] { new LongLineSegmentation.Window(0, 80), new LongLineSegmentation.Window(40, 80) };
        var first = new RecognizedText("e\u0301", .9f)
        { Characters = [new("e", .9f, 55, 58), new("\u0301", .9f, 59, 62)], StepWidth = 1 };
        var second = first with { Characters = [new("e", .9f, 15, 18), new("\u0301", .9f, 19, 22)] };
        var result = LongLineSegmentation.Stitch(windows, [first, second], 120, true);
        Assert.Equal("e\u0301", result.Text);
        Assert.Single(result.Characters!);
    }
}
