using PaddleOcrNet.Pdf;
using PaddleOcrNet.Pdf.Internal;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure tests (no PDFium, no models) for <see cref="EmbeddedTextLayer"/>: the quality gate that rejects broken
/// text layers, the Auto-mode coverage check, and the grouping of embedded characters into lines and words.
/// </summary>
public class PdfEmbeddedTextLayerTests
{
    private const double Em = 20;

    /// <summary>Lays <paramref name="text"/> out left to right, 10 px per character, on a 20 px line at (x, y).</summary>
    private static List<PdfTextChar> Run(string text, double x, double y, double advance = 10)
    {
        var chars = new List<PdfTextChar>();
        foreach (char ch in text)
        {
            chars.Add(new PdfTextChar(ch, x, y, x + advance, y + Em, Em));
            x += advance;
        }
        return chars;
    }

    [Fact]
    public void Gate_requires_at_least_20_non_whitespace_characters()
    {
        Assert.False(EmbeddedTextLayer.PassesQualityGate(Run(new string('a', 19), 0, 0)));
        Assert.True(EmbeddedTextLayer.PassesQualityGate(Run(new string('a', 20), 0, 0)));
        // Whitespace does not count towards the minimum.
        Assert.False(EmbeddedTextLayer.PassesQualityGate(Run(string.Join(' ', Enumerable.Repeat('a', 19)), 0, 0)));
    }

    [Theory]
    [InlineData('�')]
    [InlineData('')]
    [InlineData('')]
    public void Gate_rejects_five_percent_or_more_invalid_characters(char invalid)
    {
        Assert.False(EmbeddedTextLayer.PassesQualityGate(Run(new string('a', 38) + new string(invalid, 2), 0, 0)));
        Assert.True(EmbeddedTextLayer.PassesQualityGate(Run(new string('a', 39) + invalid, 0, 0)));
    }

    [Fact]
    public void Gate_requires_sixty_percent_letters_or_digits()
    {
        Assert.True(EmbeddedTextLayer.PassesQualityGate(Run(new string('a', 18) + new string('.', 12), 0, 0)));
        Assert.False(EmbeddedTextLayer.PassesQualityGate(Run(new string('a', 17) + new string('.', 13), 0, 0)));
    }

    [Fact]
    public void Coverage_is_the_share_of_the_page_covered_by_line_boxes()
    {
        var lines = EmbeddedTextLayer.BuildLines(Run(new string('a', 20), 0, 0).Concat(Run(new string('b', 10), 0, 40)).ToList());
        Assert.Equal(2, lines.Count);
        Assert.Equal(6000.0 / 1_000_000, EmbeddedTextLayer.CoverageRatio(lines, 1000, 1000), 9); // 200x20 + 100x20 px
    }

    [Fact]
    public void Modes_decide_between_embedded_text_and_ocr()
    {
        var chars = Run(new string('a', 30), 0, 0); // one 300x20 px line: 0.6% of 1000x1000, 6% of 300x333

        Assert.Null(EmbeddedTextLayer.SelectLines(chars, PdfTextLayerMode.Ignore, 300, 333));
        Assert.NotNull(EmbeddedTextLayer.SelectLines(chars, PdfTextLayerMode.PreferEmbedded, 1000, 1000));
        Assert.Null(EmbeddedTextLayer.SelectLines(chars, PdfTextLayerMode.Auto, 1000, 1000));
        Assert.NotNull(EmbeddedTextLayer.SelectLines(chars, PdfTextLayerMode.Auto, 300, 333));
        Assert.Null(EmbeddedTextLayer.SelectLines(Run(new string('�', 30), 0, 0), PdfTextLayerMode.PreferEmbedded, 300, 333));
    }

    [Fact]
    public void Characters_on_one_baseline_form_one_line_with_the_union_box()
    {
        var lines = EmbeddedTextLayer.BuildLines(Run("Hello world", 5, 7));

        var line = Assert.Single(lines);
        Assert.Equal("Hello world", line.Text);
        Assert.Equal(1.0, line.Confidence);
        Assert.Equal(new Models.OcrBoundingBox(5, 7, 115, 27), line.BoundingBox);
        Assert.Equal(4, line.BoundingPolygon.Count);
    }

    [Fact]
    public void A_gap_wider_than_three_tenths_of_the_font_height_splits_words()
    {
        var wide = Run("ab", 0, 0).Concat(Run("cd", 20 + 7, 0)).ToList();   // 7 px > 0.3 x 20
        var narrow = Run("ab", 0, 0).Concat(Run("cd", 20 + 5, 0)).ToList(); // 5 px < 6 px

        Assert.Equal("ab cd", Assert.Single(EmbeddedTextLayer.BuildLines(wide)).Text);
        Assert.Equal("abcd", Assert.Single(EmbeddedTextLayer.BuildLines(narrow)).Text);
    }

    [Fact]
    public void Letter_spaced_text_is_not_split_into_single_letters()
    {
        // Tracked heading, as in bilingual_welcome.pdf: 8 px between letters (> 0.3 x 20 px) but 30 px between words.
        var chars = new List<PdfTextChar>();
        double x = 0;
        foreach (char ch in "ДОБРО")
        {
            chars.Add(new PdfTextChar(ch, x, 0, x + 10, Em, Em));
            x += 18;
        }
        x += 22; // word gap: 30 px after the last letter
        foreach (char ch in "ПОЖ")
        {
            chars.Add(new PdfTextChar(ch, x, 0, x + 10, Em, Em));
            x += 18;
        }

        Assert.Equal("ДОБРО ПОЖ", Assert.Single(EmbeddedTextLayer.BuildLines(chars)).Text);
    }

    [Fact]
    public void Generated_line_break_characters_only_separate_words()
    {
        var chars = Run("ab", 0, 0);
        chars.Add(new PdfTextChar('\r', 0, 0, 0, 0, 0));
        chars.Add(new PdfTextChar('\n', 0, 0, 0, 0, 0));
        chars.AddRange(Run("cd", 22, 0));

        Assert.Equal("ab cd", Assert.Single(EmbeddedTextLayer.BuildLines(chars)).Text);
    }

    [Fact]
    public void A_column_sized_gap_or_a_new_baseline_starts_a_new_line()
    {
        var columns = Run("left", 0, 0).Concat(Run("right", 40 + 41, 0)).ToList(); // 41 px > 2 x 20
        var rows = Run("first", 0, 0).Concat(Run("second", 0, 30)).ToList();

        Assert.Equal(new[] { "left", "right" }, EmbeddedTextLayer.BuildLines(columns).Select(l => l.Text));
        var lines = EmbeddedTextLayer.BuildLines(rows);
        Assert.Equal(new[] { "first", "second" }, lines.Select(l => l.Text));
        Assert.Equal(30, lines[1].BoundingBox.MinY);
    }

    [Fact]
    public void Small_punctuation_inside_the_line_band_stays_on_the_line()
    {
        var chars = Run("ab", 0, 0);
        chars.Add(new PdfTextChar('.', 20, 16, 23, 20, Em));

        Assert.Equal("ab.", Assert.Single(EmbeddedTextLayer.BuildLines(chars)).Text);
    }

    [Fact]
    public void CreateResult_joins_lines_and_reports_the_page_size()
    {
        var lines = EmbeddedTextLayer.BuildLines(Run("first", 0, 0).Concat(Run("second", 0, 30)).ToList());
        var result = EmbeddedTextLayer.CreateResult(lines, 800, 600, new[] { "en" }, TimeSpan.FromMilliseconds(3));

        Assert.Equal("first\nsecond", result.FullText);
        Assert.Equal(800, result.SourceWidth);
        Assert.Equal(600, result.SourceHeight);
        Assert.Equal(new[] { "en" }, result.Languages);
    }
}
