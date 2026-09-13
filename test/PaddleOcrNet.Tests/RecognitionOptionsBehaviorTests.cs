using PaddleOcrNet.Export;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Internal.Recognition;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-free tests for recognition-stage option semantics: the service/call precedence of the text-line
/// orientation switch, <see cref="RecognitionOptions.MaxDegreeOfParallelism"/> resolution and the bounded
/// parallel helper, and the plumbing of word boxes through line grouping and the exporters.
/// </summary>
public class RecognitionOptionsBehaviorTests
{
    [Fact]
    public void Word_boxes_are_off_by_default()
    {
        Assert.False(RecognitionOptions.Default.ReturnWordBoxes);
        Assert.Empty(new OcrLine { Text = "x" }.Words);
    }

    [Theory]
    // call option (null = left at its default), service option (null = never set), classifier runs?
    [InlineData(null, null, true)]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, null, false)]
    public void Explicit_call_value_wins_then_the_service_value_then_the_default(bool? call, bool? service, bool expected)
    {
        var options = call is bool value ? new RecognitionOptions { UseTextLineOrientation = value } : RecognitionOptions.Default;

        Assert.Equal(expected, PaddleOcrEngine.ResolveUseTextLineOrientation(options, service));
    }

    [Fact]
    public void Explicitness_survives_with_copies()
    {
        Assert.True((new RecognitionOptions { UseTextLineOrientation = true } with { BatchSize = 3 }).UseTextLineOrientationSpecified);
        Assert.False((RecognitionOptions.Default with { BatchSize = 3 }).UseTextLineOrientationSpecified);
        Assert.True(RecognitionOptions.Default.UseTextLineOrientation);
    }

    [Fact]
    public void Service_orientation_option_is_unset_until_assigned()
    {
        var options = new PaddleOcrServiceOptions();
        Assert.False(options.UseTextLineOrientation);
        Assert.Null(options.ToEngineOptions().UseTextLineOrientation);

        options.UseTextLineOrientation = false;
        Assert.False(options.ToEngineOptions().UseTextLineOrientation);
    }

    [Fact]
    public void Max_degree_of_parallelism_falls_back_to_the_processor_count()
    {
        Assert.Equal(Environment.ProcessorCount, BoundedParallel.ResolveDegree(0));
        Assert.Equal(Environment.ProcessorCount, BoundedParallel.ResolveDegree(-1));
        Assert.Equal(3, BoundedParallel.ResolveDegree(3));
    }

    [Fact]
    public void Bounded_parallel_visits_every_index_once_and_rethrows_the_original_exception()
    {
        var hits = new int[200];
        BoundedParallel.For(hits.Length, 4, CancellationToken.None, i => Interlocked.Increment(ref hits[i]));
        Assert.All(hits, h => Assert.Equal(1, h));

        Assert.Throws<InvalidOperationException>(() =>
            BoundedParallel.For(16, 4, CancellationToken.None, i => { if (i == 7) throw new InvalidOperationException(); }));
        Assert.Throws<InvalidOperationException>(() =>
            BoundedParallel.For(16, 1, CancellationToken.None, i => { if (i == 7) throw new InvalidOperationException(); }));
    }

    [Fact]
    public void Line_grouping_carries_member_words_in_left_to_right_order()
    {
        var right = Line("world", 120, Word("world", 120, 180));
        var left = Line("hello", 0, Word("hello", 0, 100));

        var merged = Assert.Single(LineGrouper.Merge(new[] { right, left }));

        Assert.Equal("hello world", merged.Text);
        Assert.Equal(new[] { "hello", "world" }, merged.Words.Select(w => w.Text));
    }

    [Fact]
    public void Exporters_use_real_word_boxes_and_confidences_when_present()
    {
        var line = Line("hello world", 0, Word("hello", 10, 40, 0.5), Word("world", 50, 90, 0.25));
        var result = new OcrResult { FullText = line.Text, Lines = new[] { line }, Languages = new[] { "en" } };

        string tsv = result.ToTsv();
        Assert.Contains("\t10\t0\t30\t20\t50\thello", tsv);
        Assert.Contains("\t50\t0\t40\t20\t25\tworld", tsv);

        string hocr = result.ToHocr();
        Assert.Contains("bbox 10 0 40 20; x_wconf 50'>hello", hocr);

        // Without real words the proportional estimate (with the line confidence) is still produced.
        var estimated = new OcrResult { FullText = "a b", Lines = new[] { line with { Words = Array.Empty<OcrWord>() } }, Languages = new[] { "en" } };
        Assert.Equal(2, estimated.ToTsv().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1);
    }

    private static OcrLine Line(string text, double minX, params OcrWord[] words)
    {
        double maxX = words.Length > 0 ? words.Max(w => w.BoundingBox.MaxX) : minX + 10;
        var poly = new[] { new OcrPoint(minX, 0), new OcrPoint(maxX, 0), new OcrPoint(maxX, 20), new OcrPoint(minX, 20) };
        return new OcrLine
        {
            Text = text,
            Confidence = 0.9,
            BoundingPolygon = poly,
            BoundingBox = OcrBoundingBox.FromPoints(poly),
            Words = words,
        };
    }

    private static OcrWord Word(string text, double minX, double maxX, double confidence = 0.9)
    {
        var poly = new[] { new OcrPoint(minX, 0), new OcrPoint(maxX, 0), new OcrPoint(maxX, 20), new OcrPoint(minX, 20) };
        return new OcrWord { Text = text, Confidence = confidence, BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) };
    }
}
