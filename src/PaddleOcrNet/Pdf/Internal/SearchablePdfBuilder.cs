using System.Globalization;
using System.IO.Compression;
using System.Text;
using EasyImageSharp;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Pdf.Internal;

/// <summary>
/// Writes a searchable PDF straight to a stream: each page is its rendered image (a JPEG XObject) under an
/// invisible text layer (render mode 3).
/// <para>
/// The text uses Tesseract's approach so any script round-trips: a Type0 font with Identity-H encoding over a
/// CIDFontType2 that embeds a glyphless TrueType program (<see cref="GlyphlessFont"/>). A ToUnicode CMap
/// (<see cref="PdfTextEncoder"/>) makes the text extractable, and <c>Tz</c> horizontal scaling stretches each run
/// across its box.
/// </para>
/// <para>
/// Pages are written as they are added, with byte offsets tracked for the xref table. Only small per-page
/// bookkeeping is kept in memory, so the output stream may be non-seekable. The shared font, page tree and
/// catalog objects, which have reserved object numbers, are written by <see cref="Finish"/>.
/// </para>
/// </summary>
internal sealed class SearchablePdfBuilder
{
    private const int CatalogObj = 1;
    private const int PagesObj = 2;
    private const int FontObj = 3;
    private const int CidFontObj = 4;
    private const int FontDescriptorObj = 5;
    private const int FontFileObj = 6;
    private const int CidToGidMapObj = 7;
    private const int ToUnicodeObj = 8;
    private const int FirstPageObj = 9;

    private static readonly Lazy<byte[]> s_fontFile = new(() => Deflate(GlyphlessFont.TrueTypeProgram, CompressionLevel.SmallestSize));
    private static readonly Lazy<byte[]> s_cidToGidMap = new(() => Deflate(GlyphlessFont.BuildCidToGidMap(), CompressionLevel.SmallestSize));

    private readonly Stream _output;
    private readonly List<long> _offsets = new() { 0 }; // index = object number; 0 is the free-list head
    private readonly List<int> _pageObjects = new();
    private readonly PdfTextEncoder _encoder = new();
    private long _position;
    private int _nextObject = FirstPageObj;
    private bool _finished;

    /// <summary>
    /// Starts a document on <paramref name="output"/> and writes the header immediately.
    /// </summary>
    public SearchablePdfBuilder(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        WriteAscii("%PDF-1.7\n");
        WriteBytes(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }); // binary marker
    }

    /// <summary>
    /// Encodes a page image as the JPEG embedded by <see cref="AddPage"/>. The image is only read, so this may run
    /// concurrently with OCR on the same image.
    /// </summary>
    public static byte[] EncodeJpeg(Image<Rgb24> image, int jpegQuality)
    {
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = jpegQuality });
        return ms.ToArray();
    }

    /// <summary>
    /// Writes one page: the JPEG image scaled to the page at <paramref name="dpi"/>, and the invisible text of
    /// <paramref name="lines"/> (pixel coordinates at that DPI).
    /// </summary>
    public void AddPage(ReadOnlySpan<byte> jpeg, int pixelWidth, int pixelHeight, double dpi, IReadOnlyList<OcrLine> lines)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        double scale = 72.0 / dpi;                 // points per pixel (PDF user space is 72 dpi)
        double widthPt = pixelWidth * scale;
        double heightPt = pixelHeight * scale;

        int contentObj = _nextObject++;
        int imageObj = _nextObject++;
        int pageObj = _nextObject++;

        string content = BuildContent(lines, widthPt, heightPt, scale, _encoder);
        WriteStreamObject(contentObj, string.Empty, Deflate(Encoding.ASCII.GetBytes(content), CompressionLevel.Fastest), deflated: true);

        BeginObject(imageObj);
        WriteAscii(
            $"<< /Type /XObject /Subtype /Image /Width {pixelWidth} /Height {pixelHeight} " +
            $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        WriteBytes(jpeg);
        WriteAscii("\nendstream\nendobj\n");

        BeginObject(pageObj);
        WriteAscii(
            $"<< /Type /Page /Parent {PagesObj} 0 R " +
            $"/MediaBox [0 0 {Num(widthPt)} {Num(heightPt)}] " +
            $"/Resources << /XObject << /Im0 {imageObj} 0 R >> /Font << /F1 {FontObj} 0 R >> >> " +
            $"/Contents {contentObj} 0 R >>\nendobj\n");

        _pageObjects.Add(pageObj);
    }

    /// <summary>
    /// Writes the shared font objects, page tree, catalog, xref table and trailer, then flushes the stream.
    /// </summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;

        string name = GlyphlessFont.FontName;
        BeginObject(FontObj);
        WriteAscii(
            $"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H " +
            $"/DescendantFonts [{CidFontObj} 0 R] /ToUnicode {ToUnicodeObj} 0 R >>\nendobj\n");

        BeginObject(CidFontObj);
        WriteAscii(
            $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            $"/FontDescriptor {FontDescriptorObj} 0 R /DW {GlyphlessFont.AdvanceWidth} /CIDToGIDMap {CidToGidMapObj} 0 R >>\nendobj\n");

        BeginObject(FontDescriptorObj);
        WriteAscii(
            $"<< /Type /FontDescriptor /FontName /{name} /Flags 5 /FontBBox [0 0 {GlyphlessFont.AdvanceWidth} {GlyphlessFont.UnitsPerEm}] " +
            $"/ItalicAngle 0 /Ascent {GlyphlessFont.UnitsPerEm} /Descent -1 /CapHeight {GlyphlessFont.UnitsPerEm} /StemV 80 " +
            $"/FontFile2 {FontFileObj} 0 R >>\nendobj\n");

        WriteStreamObject(FontFileObj, $"/Length1 {GlyphlessFont.TrueTypeProgram.Length} ", s_fontFile.Value, deflated: true);
        WriteStreamObject(CidToGidMapObj, string.Empty, s_cidToGidMap.Value, deflated: true);
        WriteStreamObject(ToUnicodeObj, string.Empty,
            Deflate(Encoding.ASCII.GetBytes(_encoder.BuildToUnicodeCMap()), CompressionLevel.Optimal), deflated: true);

        BeginObject(PagesObj);
        var kids = new StringBuilder();
        foreach (int pageObj in _pageObjects)
        {
            if (kids.Length > 0) kids.Append(' ');
            kids.Append(pageObj).Append(" 0 R");
        }
        WriteAscii($"<< /Type /Pages /Count {_pageObjects.Count} /Kids [{kids}] >>\nendobj\n");

        BeginObject(CatalogObj);
        WriteAscii($"<< /Type /Catalog /Pages {PagesObj} 0 R >>\nendobj\n");

        long xrefPos = _position;
        int size = _nextObject;
        var xref = new StringBuilder(32 + size * 20);
        xref.Append("xref\n0 ").Append(size).Append('\n');
        xref.Append("0000000000 65535 f \n");
        for (int i = 1; i < size; i++)
            xref.Append(_offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        WriteAscii(xref.ToString());
        WriteAscii($"trailer\n<< /Size {size} /Root {CatalogObj} 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        _output.Flush();
    }

    /// <summary>
    /// Builds a page content stream: draw the image full-bleed, then emit the invisible text runs.
    /// </summary>
    internal static string BuildContent(IReadOnlyList<OcrLine> lines, double widthPt, double heightPt, double scale, PdfTextEncoder encoder)
    {
        var sb = new StringBuilder();
        sb.Append("q\n").Append(Num(widthPt)).Append(" 0 0 ").Append(Num(heightPt)).Append(" 0 0 cm\n/Im0 Do\nQ\n");

        sb.Append("BT\n3 Tr\n");
        foreach (var line in lines)
            AppendLine(sb, encoder, line, scale, heightPt);
        sb.Append("ET\n");
        return sb.ToString();
    }

    /// <summary>
    /// Appends the invisible text of one line. When the line carries <see cref="OcrLine.Words"/> that spell out
    /// its text, each word becomes its own run at its word box, so selection and search hits land on the word
    /// instead of a guessed slice of the line. Otherwise (no words, or words that do not match the line text) the
    /// whole line is one run across the line box.
    /// <para>
    /// Where the line text has whitespace between two words, a single space glyph is written after the first word,
    /// <c>Tz</c>-scaled on its own to span the gap up to the next word, so extracted text reads "word word" while
    /// every word's glyphs still fill exactly its box. (Tesseract's pdfrenderer instead appends the space inside
    /// the word's run and squeezes it into the word width.) No space is written where the text has none, e.g.
    /// between CJK characters, which are one word each. Like the line run, word runs are placed on the axis-aligned
    /// box, so rotated or slanted text is handled exactly as before, only at word granularity.
    /// </para>
    /// </summary>
    internal static void AppendLine(StringBuilder sb, PdfTextEncoder encoder, OcrLine line, double scale, double pageHeightPt)
    {
        var words = line.Words;
        if (words is { Count: > 0 })
        {
            var spaceAfter = new bool[words.Count];
            // RTL lines keep display-order text, so their words (in recognition order) may spell it backwards.
            bool reversed = !MatchWords(line.Text, words, reversed: false, spaceAfter);
            if (!reversed || MatchWords(line.Text, words, reversed: true, spaceAfter))
            {
                for (int k = 0; k < words.Count; k++)
                {
                    var word = words[reversed ? words.Count - 1 - k : k];
                    double size = AppendTextRun(sb, encoder, word.Text, word.BoundingBox, scale, pageHeightPt);
                    if (spaceAfter[k] && size > 0)
                    {
                        var next = words[reversed ? words.Count - 2 - k : k + 1];
                        AppendSpace(sb, encoder, word.BoundingBox, next.BoundingBox, size, scale);
                    }
                }
                return;
            }
        }

        AppendTextRun(sb, encoder, line.Text, line.BoundingBox, scale, pageHeightPt);
    }

    /// <summary>
    /// Checks that <paramref name="words"/> (in order, or reversed) spell out <paramref name="text"/> with only
    /// whitespace between and around them, and records in <paramref name="spaceAfter"/> (indexed in that order)
    /// which words are followed by whitespace.
    /// </summary>
    internal static bool MatchWords(string text, IReadOnlyList<OcrWord> words, bool reversed, bool[] spaceAfter)
    {
        int cursor = 0;
        for (int k = 0; k < words.Count; k++)
        {
            string word = words[reversed ? words.Count - 1 - k : k].Text;
            if (string.IsNullOrWhiteSpace(word)) return false;

            int start = cursor;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (string.CompareOrdinal(text, start, word, 0, word.Length) != 0 || start + word.Length > text.Length) return false;

            if (k > 0) spaceAfter[k - 1] = start > cursor;
            cursor = start + word.Length;
        }
        spaceAfter[words.Count - 1] = false;

        while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
        return cursor == text.Length;
    }

    /// <summary>
    /// Appends one invisible text run whose glyphs span <paramref name="box"/> (pixels): font size = box height,
    /// baseline on the box bottom, and <c>Tz</c> scaling so the advance widths add up to the box width.
    /// </summary>
    /// <returns>The font size (points) of the run, or 0 when nothing was written.</returns>
    internal static double AppendTextRun(StringBuilder sb, PdfTextEncoder encoder, string text, OcrBoundingBox box, double scale, double pageHeightPt)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var hex = new StringBuilder(text.Length * 4);
        int glyphs = encoder.Encode(text, hex);
        if (glyphs == 0) return 0;

        double size = Math.Max(1.0, box.Height * scale);
        double naturalWidth = glyphs * size * GlyphlessFont.AdvanceWidth / GlyphlessFont.UnitsPerEm;
        double boxWidth = box.Width * scale;
        double horizontalScale = boxWidth > 0 ? Math.Clamp(100.0 * boxWidth / naturalWidth, 0.1, 100_000) : 100.0;
        double x = box.MinX * scale;
        double yBaseline = pageHeightPt - box.MaxY * scale; // PDF origin is bottom-left

        sb.Append("/F1 ").Append(Num(size)).Append(" Tf\n");
        sb.Append(Num(horizontalScale)).Append(" Tz\n");
        sb.Append("1 0 0 1 ").Append(Num(x)).Append(' ').Append(Num(yBaseline)).Append(" Tm\n");
        sb.Append('<').Append(hex).Append("> Tj\n");
        return size;
    }

    /// <summary>
    /// Appends a space glyph right after a word run (the text position is then at the word box's right edge),
    /// scaled to reach the next word's left edge. When the next word does not lie to the right (a wrapped,
    /// flipped or vertical line), the space gets a small fixed width instead.
    /// </summary>
    private static void AppendSpace(StringBuilder sb, PdfTextEncoder encoder, OcrBoundingBox word, OcrBoundingBox next, double size, double scale)
    {
        var hex = new StringBuilder(4);
        encoder.Encode(" ", hex);
        double naturalWidth = size * GlyphlessFont.AdvanceWidth / GlyphlessFont.UnitsPerEm;
        double gap = Math.Max((next.MinX - word.MaxX) * scale, 0.1 * size);
        double horizontalScale = Math.Clamp(100.0 * gap / naturalWidth, 0.1, 100_000);
        sb.Append(Num(horizontalScale)).Append(" Tz\n");
        sb.Append('<').Append(hex).Append("> Tj\n");
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static byte[] Deflate(byte[] data, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private void BeginObject(int number)
    {
        while (_offsets.Count <= number) _offsets.Add(0);
        _offsets[number] = _position;
        WriteAscii($"{number} 0 obj\n");
    }

    private void WriteStreamObject(int number, string extraDictEntries, byte[] data, bool deflated)
    {
        BeginObject(number);
        string filter = deflated ? "/Filter /FlateDecode " : string.Empty;
        WriteAscii($"<< {extraDictEntries}{filter}/Length {data.Length} >>\nstream\n");
        WriteBytes(data);
        WriteAscii("\nendstream\nendobj\n");
    }

    private void WriteAscii(string s) => WriteBytes(Encoding.ASCII.GetBytes(s));

    private void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        _output.Write(bytes);
        _position += bytes.Length;
    }
}
