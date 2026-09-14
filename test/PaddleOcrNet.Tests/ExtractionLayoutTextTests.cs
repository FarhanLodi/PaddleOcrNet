using PaddleOcrNet.Extraction;
using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-free tests for <see cref="LayoutTextExtensions.ToLayoutText"/>. Boxes are 10 px per character so
/// the estimated cell width is exactly 10.
/// </summary>
public class ExtractionLayoutTextTests
{
    private static OcrLine Line(string text, double x1, double y1, double x2, double y2) => new()
    {
        Text = text,
        Confidence = 0.9,
        BoundingBox = new OcrBoundingBox(x1, y1, x2, y2),
    };

    private static OcrResult Result(params OcrLine[] lines) => new()
    {
        FullText = string.Join('\n', lines.Select(l => l.Text)),
        Lines = lines,
        Languages = new[] { "en" },
    };

    [Fact]
    public void Two_columns_stay_side_by_side_regardless_of_line_order()
    {
        var result = Result(
            Line("Right one", 400, 2, 490, 22),
            Line("Left two", 0, 30, 80, 50),
            Line("Left one", 0, 0, 80, 20),
            Line("Right two", 400, 31, 490, 51));

        string expected =
            "Left one".PadRight(40) + "Right one" + "\n" +
            "Left two".PadRight(40) + "Right two";

        Assert.Equal(expected, result.ToLayoutText());
    }

    [Fact]
    public void Receipt_keeps_prices_aligned_and_collapses_blank_rows()
    {
        var result = Result(
            Line("STORE", 100, 0, 150, 20),
            Line("Coffee", 0, 40, 60, 60),
            Line("3.50", 260, 40, 300, 60),
            Line("Tea", 0, 70, 30, 90),
            Line("2.00", 260, 70, 300, 90),
            Line("TOTAL", 0, 130, 50, 150),
            Line("5.50", 260, 130, 300, 150));

        string expected =
            new string(' ', 10) + "STORE" + "\n" +
            "\n" +
            "Coffee".PadRight(26) + "3.50" + "\n" +
            "Tea".PadRight(26) + "2.00" + "\n" +
            "\n" +
            "TOTAL".PadRight(26) + "5.50";

        Assert.Equal(expected, result.ToLayoutText());
        Assert.DoesNotContain("\n\n", result.ToLayoutText(new LayoutTextOptions { MaxBlankLines = 0 }));
    }

    [Fact]
    public void Cjk_characters_occupy_two_columns()
    {
        var result = Result(Line("合计", 0, 0, 40, 20), Line("12.00", 200, 0, 250, 20));
        Assert.Equal("合计" + new string(' ', 16) + "12.00", result.ToLayoutText());
    }

    [Fact]
    public void Rtl_lines_are_anchored_at_their_right_edge()
    {
        const string arabic = "مرحبا";
        var result = Result(Line(arabic, 200, 0, 300, 20), Line("Hello", 0, 40, 50, 60));

        string anchored = result.ToLayoutText(new LayoutTextOptions { CharacterWidth = 10 });
        string leftAligned = result.ToLayoutText(new LayoutTextOptions { CharacterWidth = 10, RightAlignRtlLines = false });

        Assert.Equal(new string(' ', 25) + arabic, anchored.Split('\n')[0]);
        Assert.Equal(new string(' ', 20) + arabic, leftAligned.Split('\n')[0]);
    }

    [Fact]
    public void Slightly_skewed_lines_share_a_row()
    {
        var result = Result(Line("AAAAAAAAAA", 0, 0, 100, 20), Line("BBBBBBBBBB", 300, 8, 400, 28));
        Assert.Equal("AAAAAAAAAA".PadRight(30) + "BBBBBBBBBB", result.ToLayoutText());
    }

    [Fact]
    public void Colliding_lines_in_a_row_are_separated_by_a_space()
    {
        var result = Result(Line("abcdef", 0, 0, 60, 20), Line("xyz", 55, 0, 85, 20));
        Assert.Equal("abcdef xyz", result.ToLayoutText());
    }

    [Fact]
    public void Empty_and_geometry_free_results_degrade_gracefully()
    {
        Assert.Equal(string.Empty, OcrResult.Empty.ToLayoutText());

        var noBoxes = Result(new OcrLine { Text = "one" }, new OcrLine { Text = "two" });
        Assert.Equal("one\ntwo", noBoxes.ToLayoutText());
    }

    [Fact]
    public void Invalid_options_throw()
    {
        var result = Result(Line("x", 0, 0, 10, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.ToLayoutText(new LayoutTextOptions { MaxBlankLines = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.ToLayoutText(new LayoutTextOptions { CharacterWidth = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.ToLayoutText(new LayoutTextOptions { RowOverlapThreshold = 0 }));
    }
}
