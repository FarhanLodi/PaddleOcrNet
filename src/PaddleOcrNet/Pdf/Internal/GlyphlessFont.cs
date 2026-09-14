using System.Text;

namespace PaddleOcrNet.Pdf.Internal;

/// <summary>
/// A tiny TrueType program with no visible glyphs, modeled on Tesseract's <c>GlyphLessFont</c>. Every CID maps
/// to glyph 1 (via <see cref="BuildCidToGidMap"/>), which has a fixed advance of
/// <see cref="AdvanceWidth"/>/<see cref="UnitsPerEm"/>. Embedding it keeps the invisible text layer valid for any
/// script, while text extraction relies on the ToUnicode CMap.
/// <para>
/// Glyph 1's outline is a single zero-area contour (a diagonal from (0, 0) to (advance, em)), so it paints nothing,
/// but its control box covers the whole glyph cell. PDFium measures text objects and character boxes from glyph
/// boxes: with an empty glyph, a one-glyph run (a CJK character, a one-letter word, a lone space) measured zero wide
/// and was dropped from the extracted text, and every character box had zero height.
/// </para>
/// </summary>
internal static class GlyphlessFont
{
    /// <summary>Font design units per em.</summary>
    public const int UnitsPerEm = 1000;

    /// <summary>Advance width of every glyph, in design units. Also the CIDFont's <c>/DW</c>.</summary>
    public const int AdvanceWidth = 500;

    /// <summary>The PostScript/base font name.</summary>
    public const string FontName = "GlyphLessFont";

    private static readonly Lazy<byte[]> s_program = new(BuildProgram);

    /// <summary>The TrueType font program bytes (uncompressed).</summary>
    public static byte[] TrueTypeProgram => s_program.Value;

    /// <summary>
    /// Builds the uncompressed CIDToGIDMap stream: two big-endian bytes per CID (0..65535), all mapping to glyph 1.
    /// </summary>
    public static byte[] BuildCidToGidMap()
    {
        var map = new byte[65536 * 2];
        for (int i = 1; i < map.Length; i += 2) map[i] = 1;
        return map;
    }

    /// <summary>
    /// Computes the TrueType checksum (sum of big-endian uint32 words, zero-padded) of <paramref name="data"/>.
    /// </summary>
    internal static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (int k = 0; k < 4; k++)
                word = (word << 8) | (i + k < data.Length ? data[i + k] : (byte)0);
            unchecked { sum += word; }
        }
        return sum;
    }

    private static byte[] BuildProgram()
    {
        // Table tags must be sorted; all are lowercase ASCII, so ordinal order is correct.
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(),
            ["glyf"] = Glyf(),
            ["head"] = Head(),
            ["hhea"] = Hhea(),
            ["hmtx"] = Hmtx(),
            ["loca"] = Loca(),
            ["maxp"] = Maxp(),
            ["name"] = NameTable(),
            ["post"] = Post(),
        };

        int count = tables.Count;
        int entrySelector = (int)Math.Floor(Math.Log2(count));
        int searchRange = (1 << entrySelector) * 16;

        var ms = new MemoryStream();
        U32(ms, 0x00010000);
        U16(ms, count);
        U16(ms, searchRange);
        U16(ms, entrySelector);
        U16(ms, count * 16 - searchRange);

        int offset = 12 + 16 * count;
        int headOffset = 0;
        foreach (var (tag, data) in tables)
        {
            ms.Write(Encoding.ASCII.GetBytes(tag));
            U32(ms, Checksum(data));
            U32(ms, (uint)offset);
            U32(ms, (uint)data.Length);
            if (tag == "head") headOffset = offset;
            offset += (data.Length + 3) & ~3;
        }

        foreach (var data in tables.Values)
        {
            ms.Write(data);
            for (int pad = data.Length; (pad & 3) != 0; pad++) ms.WriteByte(0);
        }

        byte[] font = ms.ToArray();
        uint adjustment = unchecked(0xB1B0AFBA - Checksum(font));
        font[headOffset + 8] = (byte)(adjustment >> 24);
        font[headOffset + 9] = (byte)(adjustment >> 16);
        font[headOffset + 10] = (byte)(adjustment >> 8);
        font[headOffset + 11] = (byte)adjustment;
        return font;
    }

    private static byte[] Head()
    {
        var ms = new MemoryStream();
        U32(ms, 0x00010000);          // version
        U32(ms, 0x00010000);          // fontRevision
        U32(ms, 0);                   // checkSumAdjustment (patched after assembly)
        U32(ms, 0x5F0F3CF5);          // magicNumber
        U16(ms, 0x000B);              // flags: baseline at y=0, lsb at x=0, integer ppem
        U16(ms, UnitsPerEm);
        U32(ms, 0); U32(ms, 0);       // created
        U32(ms, 0); U32(ms, 0);       // modified
        U16(ms, 0); U16(ms, 0);       // xMin, yMin
        U16(ms, AdvanceWidth);        // xMax
        U16(ms, UnitsPerEm);          // yMax
        U16(ms, 0);                   // macStyle
        U16(ms, 3);                   // lowestRecPPEM
        U16(ms, 2);                   // fontDirectionHint
        U16(ms, 0);                   // indexToLocFormat: short offsets
        U16(ms, 0);                   // glyphDataFormat
        return ms.ToArray();
    }

    private static byte[] Hhea()
    {
        var ms = new MemoryStream();
        U32(ms, 0x00010000);          // version
        U16(ms, UnitsPerEm);          // ascender
        U16(ms, 0);                   // descender
        U16(ms, 0);                   // lineGap
        U16(ms, AdvanceWidth);        // advanceWidthMax
        U16(ms, 0); U16(ms, 0);       // minLeftSideBearing, minRightSideBearing
        U16(ms, AdvanceWidth);        // xMaxExtent
        U16(ms, 1); U16(ms, 0);       // caretSlopeRise, caretSlopeRun
        U16(ms, 0);                   // caretOffset
        U16(ms, 0); U16(ms, 0); U16(ms, 0); U16(ms, 0); // reserved
        U16(ms, 0);                   // metricDataFormat
        U16(ms, 2);                   // numberOfHMetrics
        return ms.ToArray();
    }

    private static byte[] Hmtx()
    {
        var ms = new MemoryStream();
        for (int glyph = 0; glyph < 2; glyph++)
        {
            U16(ms, AdvanceWidth);
            U16(ms, 0);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Glyph 0 (.notdef) is empty; glyph 1 is a simple glyph with one contour of two on-curve points, (0, 0) and
    /// (<see cref="AdvanceWidth"/>, <see cref="UnitsPerEm"/>): zero area, full-cell control box.
    /// </summary>
    private static byte[] Glyf()
    {
        var ms = new MemoryStream();
        U16(ms, 1);                   // numberOfContours
        U16(ms, 0); U16(ms, 0);       // xMin, yMin
        U16(ms, AdvanceWidth);        // xMax
        U16(ms, UnitsPerEm);          // yMax
        U16(ms, 1);                   // endPtsOfContours[0]
        U16(ms, 0);                   // instructionLength
        ms.WriteByte(0x01);           // flags: on curve, int16 x and y deltas
        ms.WriteByte(0x01);
        U16(ms, 0); U16(ms, AdvanceWidth);   // x deltas
        U16(ms, 0); U16(ms, UnitsPerEm);     // y deltas
        return ms.ToArray();
    }

    // Short offsets (actual offset / 2): glyph 0 is empty at 0, glyph 1 spans the whole 24-byte glyf table.
    private static byte[] Loca()
    {
        var ms = new MemoryStream();
        U16(ms, 0);
        U16(ms, 0);
        U16(ms, Glyf().Length / 2);
        return ms.ToArray();
    }

    private static byte[] Maxp()
    {
        var ms = new MemoryStream();
        U32(ms, 0x00010000);          // version 1.0
        U16(ms, 2);                   // numGlyphs
        U16(ms, 2); U16(ms, 1);       // maxPoints, maxContours
        U16(ms, 0); U16(ms, 0);       // maxCompositePoints, maxCompositeContours
        U16(ms, 2);                   // maxZones
        for (int i = 0; i < 9; i++) U16(ms, 0);
        return ms.ToArray();
    }

    private static byte[] Cmap()
    {
        var ms = new MemoryStream();
        U16(ms, 0);                   // version
        U16(ms, 1);                   // numTables
        U16(ms, 3); U16(ms, 1);       // Windows, Unicode BMP
        U32(ms, 12);                  // subtable offset
        // Format 4 with only the mandatory terminating segment (maps nothing; CIDToGIDMap does the mapping).
        U16(ms, 4);                   // format
        U16(ms, 24);                  // length
        U16(ms, 0);                   // language
        U16(ms, 2);                   // segCountX2
        U16(ms, 2);                   // searchRange
        U16(ms, 0);                   // entrySelector
        U16(ms, 0);                   // rangeShift
        U16(ms, 0xFFFF);              // endCode
        U16(ms, 0);                   // reservedPad
        U16(ms, 0xFFFF);              // startCode
        U16(ms, 1);                   // idDelta
        U16(ms, 0);                   // idRangeOffset
        return ms.ToArray();
    }

    private static byte[] NameTable()
    {
        byte[] value = Encoding.BigEndianUnicode.GetBytes(FontName);
        var ms = new MemoryStream();
        U16(ms, 0);                   // format
        U16(ms, 2);                   // count
        U16(ms, 6 + 2 * 12);          // stringOffset
        foreach (int nameId in new[] { 1, 6 }) // family, PostScript name
        {
            U16(ms, 3); U16(ms, 1); U16(ms, 0x0409);
            U16(ms, nameId);
            U16(ms, value.Length);
            U16(ms, 0);
        }
        ms.Write(value);
        return ms.ToArray();
    }

    private static byte[] Post()
    {
        var ms = new MemoryStream();
        U32(ms, 0x00030000);          // version 3: no glyph names
        U32(ms, 0);                   // italicAngle
        U16(ms, 0); U16(ms, 0);       // underlinePosition, underlineThickness
        U32(ms, 1);                   // isFixedPitch
        U32(ms, 0); U32(ms, 0); U32(ms, 0); U32(ms, 0);
        return ms.ToArray();
    }

    private static void U16(Stream s, int value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    private static void U32(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }
}
