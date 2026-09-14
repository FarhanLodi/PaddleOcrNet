using System.Globalization;
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
/// Model-free tests for the searchable PDF text layer: Identity-H encoding, the ToUnicode CMap, the glyphless
/// TrueType program, <c>Tz</c> scaling, the streamed xref table, and a PDFium round trip of non-Latin-1 text.
/// </summary>
public class PdfSearchablePdfTests
{
    private static OcrLine Line(string text, double left, double top, double right, double bottom) => new()
    {
        Text = text,
        Confidence = 0.9,
        BoundingBox = new OcrBoundingBox(left, top, right, bottom),
        BoundingPolygon = new[] { new OcrPoint(left, top), new OcrPoint(right, top), new OcrPoint(right, bottom), new OcrPoint(left, bottom) },
    };

    private static byte[] BuildPdf(IReadOnlyList<OcrLine> lines, int width, int height, int dpi, Func<Stream, Stream>? wrap = null)
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24(255, 255, 255));
        byte[] jpeg = SearchablePdfBuilder.EncodeJpeg(image, 75);
        using var ms = new MemoryStream();
        var builder = new SearchablePdfBuilder(wrap?.Invoke(ms) ?? ms);
        builder.AddPage(jpeg, width, height, dpi, lines);
        builder.Finish();
        return ms.ToArray();
    }

    [Fact]
    public void Bmp_text_encodes_each_utf16_unit_as_its_own_cid()
    {
        var encoder = new PdfTextEncoder();
        var hex = new StringBuilder();

        int glyphs = encoder.Encode("A€é你", hex);

        Assert.Equal(4, glyphs);
        Assert.Equal("004120AC00E94F60", hex.ToString());
        Assert.Empty(encoder.SupplementaryCids);
    }

    [Fact]
    public void Supplementary_code_points_get_one_reusable_cid_from_the_surrogate_block()
    {
        var encoder = new PdfTextEncoder();
        var hex = new StringBuilder();

        int glyphs = encoder.Encode("😀x😀🎉", hex);

        Assert.Equal(4, glyphs); // one glyph per code point, not per UTF-16 unit
        Assert.Equal("D8000078D800D801", hex.ToString());
        Assert.Equal(0xD800, encoder.SupplementaryCids[0x1F600]);
        Assert.Equal(0xD801, encoder.SupplementaryCids[0x1F389]);
    }

    [Fact]
    public void Lone_surrogates_become_replacement_and_controls_become_spaces()
    {
        var hex = new StringBuilder();
        new PdfTextEncoder().Encode("\uD800a\tb", hex);
        Assert.Equal("FFFD006100200062", hex.ToString());
    }

    [Fact]
    public void ToUnicode_cmap_maps_bmp_ranges_and_assigned_surrogate_pairs()
    {
        var encoder = new PdfTextEncoder();
        encoder.Encode("😀", new StringBuilder());

        string cmap = encoder.BuildToUnicodeCMap();

        Assert.Contains("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange", cmap);
        Assert.Contains("<0000> <00FF> <0000>", cmap);
        Assert.Contains("<FF00> <FFFF> <FF00>", cmap);
        Assert.DoesNotContain("<D800> <D8FF>", cmap);
        Assert.Contains("1 beginbfchar\n<D800> <D83DDE00>\nendbfchar", cmap);
        // 248 identity ranges (256 minus the surrogate block) split into blocks of at most 100.
        Assert.Contains("100 beginbfrange", cmap);
        Assert.Contains("48 beginbfrange", cmap);
    }

    [Fact]
    public void Glyphless_font_is_a_well_formed_truetype_program()
    {
        byte[] font = GlyphlessFont.TrueTypeProgram;

        Assert.Equal(0x00010000u, ReadU32(font, 0));
        int numTables = (font[4] << 8) | font[5];
        Assert.Equal(9, numTables);

        var tags = new List<string>();
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + 16 * i;
            string tag = Encoding.ASCII.GetString(font, rec, 4);
            uint offset = ReadU32(font, rec + 8);
            uint length = ReadU32(font, rec + 12);
            Assert.True(offset + length <= font.Length, $"table {tag} out of bounds");
            if (tag != "head")
                Assert.Equal(ReadU32(font, rec + 4), GlyphlessFont.Checksum(font.AsSpan((int)offset, (int)length)));
            else
                Assert.Equal(0x5F0F3CF5u, ReadU32(font, (int)offset + 12));
            tags.Add(tag);
        }

        Assert.Equal(tags.OrderBy(t => t, StringComparer.Ordinal), tags);
        Assert.Equal(0xB1B0AFBAu, GlyphlessFont.Checksum(font)); // whole-font checksum after checkSumAdjustment
    }

    [Fact]
    public void Cid_to_gid_map_sends_every_cid_to_glyph_one()
    {
        byte[] map = GlyphlessFont.BuildCidToGidMap();
        Assert.Equal(131072, map.Length);
        Assert.All(Enumerable.Range(0, 65536), cid => Assert.Equal(1, (map[cid * 2] << 8) | map[cid * 2 + 1]));
    }

    [Fact]
    public void Text_run_is_invisible_hex_encoded_and_scaled_to_the_box_width()
    {
        var sb = new StringBuilder();
        // 72 DPI (scale 1): a 20 pt high box, 100 pt wide, holding 4 glyphs of natural width 4 x 10 pt = 40 pt.
        SearchablePdfBuilder.AppendTextRun(sb, new PdfTextEncoder(), "Ab€d", new OcrBoundingBox(10, 30, 110, 50), 1.0, 200);

        Assert.Equal("/F1 20 Tf\n250 Tz\n1 0 0 1 10 150 Tm\n<0041006220AC0064> Tj\n", sb.ToString());
    }

    [Fact]
    public void Content_stream_uses_render_mode_3()
    {
        string content = SearchablePdfBuilder.BuildContent(new[] { Line("x", 0, 0, 10, 10) }, 100, 100, 1.0, new PdfTextEncoder());
        Assert.Contains("BT\n3 Tr\n", content);
        Assert.StartsWith("q\n100 0 0 100 0 0 cm\n/Im0 Do\nQ\n", content);
    }

    [Fact]
    public void Streamed_xref_offsets_point_at_their_objects_even_for_a_non_seekable_output()
    {
        byte[] pdf = BuildPdf(new[] { Line("hello", 10, 10, 200, 40) }, 400, 300, 150, s => new PdfNonSeekableStream(s));
        string latin1 = Encoding.Latin1.GetString(pdf);

        int startxref = latin1.LastIndexOf("startxref\n", StringComparison.Ordinal);
        long xrefPos = long.Parse(latin1[(startxref + 10)..].Split('\n')[0], CultureInfo.InvariantCulture);
        Assert.StartsWith("xref\n0 ", latin1[(int)xrefPos..]);

        string[] header = latin1[(int)xrefPos..].Split('\n');
        int size = int.Parse(header[1].Split(' ')[1], CultureInfo.InvariantCulture);
        for (int obj = 1; obj < size; obj++)
        {
            long offset = long.Parse(header[2 + obj][..10], CultureInfo.InvariantCulture);
            Assert.StartsWith($"{obj} 0 obj\n", latin1[(int)offset..]);
        }
    }

    [Fact]
    public void Pdfium_extracts_accented_currency_cjk_and_emoji_text_at_the_ocr_boxes()
    {
        const int dpi = 200;
        var lines = new[]
        {
            Line("Café €5 naïve", 100, 100, 700, 160),
            Line("你好世界", 100, 300, 500, 380),
            Line("Smile 😀 ok", 100, 500, 600, 560),
        };
        byte[] pdf = BuildPdf(lines, 1600, 800, dpi);

        using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(dpi / 72.0));
        using var page = reader.GetPageReader(0);
        Assert.InRange(page.GetPageWidth(), 1599, 1600);

        string text = page.GetText();
        Assert.Contains("Café €5 naïve", text);
        Assert.Contains("你好世界", text);
        Assert.Contains("Smile 😀 ok", text);

        // Every extracted glyph box must sit on the OCR box it came from.
        var glyphs = page.GetCharacters().Where(c => !char.IsWhiteSpace(c.Char) && !char.IsControl(c.Char)).ToList();
        Assert.NotEmpty(glyphs);
        foreach (var g in glyphs)
        {
            double cx = (g.Box.Left + g.Box.Right) / 2.0, cy = (g.Box.Top + g.Box.Bottom) / 2.0;
            Assert.Contains(lines, l =>
                cx >= l.BoundingBox.MinX - 5 && cx <= l.BoundingBox.MaxX + 5 &&
                cy >= l.BoundingBox.MinY - 0.25 * l.BoundingBox.Height && cy <= l.BoundingBox.MaxY + 0.25 * l.BoundingBox.Height);
        }

        // Tz scaling: each line's glyphs span (nearly) its whole box width.
        foreach (var line in lines)
        {
            var inLine = glyphs.Where(g => (g.Box.Top + g.Box.Bottom) / 2.0 >= line.BoundingBox.MinY - 20
                && (g.Box.Top + g.Box.Bottom) / 2.0 <= line.BoundingBox.MaxY + 20).ToList();
            double span = inLine.Max(g => g.Box.Right) - inLine.Min(g => g.Box.Left);
            Assert.InRange(span, 0.85 * line.BoundingBox.Width, 1.05 * line.BoundingBox.Width);
        }
    }

    private static uint ReadU32(byte[] data, int offset)
        => (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
