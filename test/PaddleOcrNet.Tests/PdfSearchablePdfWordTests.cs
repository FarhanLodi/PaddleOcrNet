using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;
using PaddleOcrNet.Pdf.Internal;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-free tests for per-word placement in the searchable PDF text layer: one run per word box, a gap-spanning
/// space only where the line text has whitespace (none between CJK characters), and the per-line fallback.
/// </summary>
public class PdfSearchablePdfWordTests
{
    private static OcrBoundingBox Box(double left, double top, double right, double bottom) => new(left, top, right, bottom);

    private static OcrWord Word(string text, double left, double top, double right, double bottom) => new()
    {
        Text = text,
        Confidence = 0.9,
        BoundingBox = Box(left, top, right, bottom),
        BoundingPolygon = new[] { new OcrPoint(left, top), new OcrPoint(right, top), new OcrPoint(right, bottom), new OcrPoint(left, bottom) },
    };

    private static OcrLine Line(string text, params OcrWord[] words)
    {
        var box = words.Length == 0
            ? Box(10, 30, 110, 50)
            : Box(words.Min(w => w.BoundingBox.MinX), words.Min(w => w.BoundingBox.MinY), words.Max(w => w.BoundingBox.MaxX), words.Max(w => w.BoundingBox.MaxY));
        return new OcrLine { Text = text, Confidence = 0.9, BoundingBox = box, Words = words };
    }

    private static string Content(params OcrLine[] lines) => SearchablePdfBuilder.BuildContent(lines, 200, 200, 1.0, new PdfTextEncoder());

    private static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Each_word_is_its_own_run_with_a_space_scaled_to_the_gap()
    {
        var sb = new StringBuilder();
        // 72 DPI (scale 1), page 200 pt high. Each word: 20 pt high, 2 glyphs of natural width 20 pt over 40 pt -> 200 Tz.
        // The space (natural width 10 pt) spans the 10 pt gap from x=50 to x=60 -> 100 Tz.
        var line = Line("Ab cd", Word("Ab", 10, 30, 50, 50), Word("cd", 60, 30, 100, 50));

        SearchablePdfBuilder.AppendLine(sb, new PdfTextEncoder(), line, 1.0, 200);

        Assert.Equal(
            "/F1 20 Tf\n200 Tz\n1 0 0 1 10 150 Tm\n<00410062> Tj\n" +
            "100 Tz\n<0020> Tj\n" +
            "/F1 20 Tf\n200 Tz\n1 0 0 1 60 150 Tm\n<00630064> Tj\n",
            sb.ToString());
    }

    [Fact]
    public void Cjk_words_get_no_space_and_mixed_lines_follow_the_line_text()
    {
        string cjk = Content(Line("你好", Word("你", 10, 30, 30, 50), Word("好", 30, 30, 50, 50)));
        Assert.Equal(2, Count(cjk, " Tm\n"));
        Assert.DoesNotContain("<0020>", cjk);

        // "价格" are one word each and touch; "USD" is separated from them by a space in the line text.
        string mixed = Content(Line("价格 USD", Word("价", 10, 30, 30, 50), Word("格", 30, 30, 50, 50), Word("USD", 60, 30, 110, 50)));
        Assert.Equal(3, Count(mixed, " Tm\n"));
        Assert.Equal(1, Count(mixed, "<0020>"));
        Assert.True(mixed.IndexOf("<0020>", StringComparison.Ordinal) > mixed.IndexOf("<683C>", StringComparison.Ordinal));
    }

    [Fact]
    public void Lines_without_matching_words_fall_back_to_one_line_run()
    {
        Assert.Equal(1, Count(Content(Line("hello world")), " Tm\n"));

        // Words that do not spell out the line text (here a missing character) must not replace it.
        var mismatched = Line("hello world", Word("hello", 10, 30, 60, 50), Word("wrld", 70, 30, 110, 50));
        string content = Content(mismatched);
        Assert.Equal(1, Count(content, " Tm\n"));
        Assert.Contains("1 0 0 1 10 150 Tm", content);
    }

    [Fact]
    public void Words_spelling_the_line_backwards_are_written_in_line_text_order()
    {
        // Right-to-left lines keep display-order text while their words stay in recognition order.
        var line = Line("cd Ab", Word("Ab", 60, 30, 100, 50), Word("cd", 10, 30, 50, 50));

        string content = Content(line);

        Assert.Equal(2, Count(content, " Tm\n"));
        Assert.True(content.IndexOf("<00630064>", StringComparison.Ordinal) < content.IndexOf("<0020>", StringComparison.Ordinal));
        Assert.True(content.IndexOf("<0020>", StringComparison.Ordinal) < content.IndexOf("<00410062>", StringComparison.Ordinal));
    }

    [Fact]
    public void Match_words_records_whitespace_between_words_only()
    {
        var words = new[] { Word("a", 0, 0, 1, 1), Word("中", 0, 0, 1, 1), Word("文", 0, 0, 1, 1), Word("b", 0, 0, 1, 1) };
        var spaces = new bool[words.Length];

        Assert.True(SearchablePdfBuilder.MatchWords("  a 中文\tb ", words, reversed: false, spaces));
        Assert.Equal(new[] { true, false, true, false }, spaces);
        Assert.False(SearchablePdfBuilder.MatchWords("a 中文 b c", words, reversed: false, spaces));
        Assert.False(SearchablePdfBuilder.MatchWords("a 中x文 b", words, reversed: false, spaces));
    }

    [Fact]
    public void Pdfium_keeps_one_glyph_runs_and_gives_glyphs_the_box_height()
    {
        // A one-glyph run used to measure zero wide in PDFium (the glyphless glyph had an empty box) and was dropped.
        var lines = new[] { new OcrLine { Text = "7", Confidence = 0.9, BoundingBox = Box(100, 100, 130, 160) } };

        byte[] pdf;
        using (var image = new Image<Rgb24>(400, 300, new Rgb24(255, 255, 255)))
        using (var ms = new MemoryStream())
        {
            var builder = new SearchablePdfBuilder(ms);
            builder.AddPage(SearchablePdfBuilder.EncodeJpeg(image, 75), 400, 300, 72, lines);
            builder.Finish();
            pdf = ms.ToArray();
        }

        using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(1.0));
        using var page = reader.GetPageReader(0);
        Assert.Contains("7", page.GetText());
        var glyph = Assert.Single(page.GetCharacters(), c => c.Char == '7');
        Assert.InRange(glyph.Box.Bottom - glyph.Box.Top, 55, 61);
        Assert.InRange(glyph.Box.Right - glyph.Box.Left, 28, 31);
    }

    [Fact]
    public void Pdfium_reads_spaced_latin_words_and_unspaced_cjk_at_their_word_boxes()
    {
        const int dpi = 144;
        var lines = new[]
        {
            Line("Hello brave world", Word("Hello", 100, 100, 300, 150), Word("brave", 330, 100, 520, 150), Word("world", 560, 100, 760, 150)),
            Line("你好世界", Word("你", 100, 300, 160, 360), Word("好", 160, 300, 220, 360), Word("世", 220, 300, 280, 360), Word("界", 280, 300, 340, 360)),
        };

        byte[] pdf;
        using (var image = new Image<Rgb24>(1000, 500, new Rgb24(255, 255, 255)))
        using (var ms = new MemoryStream())
        {
            var builder = new SearchablePdfBuilder(ms);
            builder.AddPage(SearchablePdfBuilder.EncodeJpeg(image, 75), 1000, 500, dpi, lines);
            builder.Finish();
            pdf = ms.ToArray();
        }

        using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(dpi / 72.0));
        using var page = reader.GetPageReader(0);
        string text = page.GetText();
        Assert.Contains("Hello brave world", text);
        Assert.Contains("你好世界", text);

        var glyphs = page.GetCharacters().Where(c => !char.IsWhiteSpace(c.Char) && !char.IsControl(c.Char)).ToList();
        var words = lines.SelectMany(l => l.Words).ToList();
        Assert.Equal(words.Sum(w => w.Text.Length), glyphs.Count);

        int index = 0;
        foreach (var word in words)
        {
            var own = glyphs.Skip(index).Take(word.Text.Length).ToList();
            index += word.Text.Length;
            Assert.Equal(word.Text, new string(own.Select(g => g.Char).ToArray()));
            foreach (var g in own)
            {
                double cx = (g.Box.Left + g.Box.Right) / 2.0, cy = (g.Box.Top + g.Box.Bottom) / 2.0;
                Assert.InRange(cx, word.BoundingBox.MinX, word.BoundingBox.MaxX);
                Assert.InRange(cy, word.BoundingBox.MinY - 0.25 * word.BoundingBox.Height, word.BoundingBox.MaxY + 0.25 * word.BoundingBox.Height);
            }
            double span = own.Max(g => g.Box.Right) - own.Min(g => g.Box.Left);
            Assert.InRange(span, 0.85 * word.BoundingBox.Width, 1.05 * word.BoundingBox.Width);
        }
    }
}
