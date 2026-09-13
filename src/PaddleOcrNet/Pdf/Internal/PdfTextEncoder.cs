using System.Globalization;
using System.Text;

namespace PaddleOcrNet.Pdf.Internal;

/// <summary>
/// Encodes text for the searchable PDF's Type0 / Identity-H glyphless font and builds the matching ToUnicode CMap.
/// <para>
/// A BMP code point is its own CID (its UTF-16 code unit). The surrogate block <c>D800–DFFF</c> never occurs as
/// a standalone code unit, so its 2048 CIDs are assigned on demand to supplementary code points (emoji, rare CJK).
/// Each such CID maps in the CMap to the full surrogate pair. That gives one glyph per code point, which keeps the
/// <c>Tz</c> width scaling right. Lone surrogates become U+FFFD, and C0/C1 controls become spaces.
/// </para>
/// One instance serves a whole document, because the CMap is written once, after the last page.
/// </summary>
internal sealed class PdfTextEncoder
{
    /// <summary>First CID reused for supplementary code points.</summary>
    internal const int FirstSupplementaryCid = 0xD800;

    /// <summary>Last CID reused for supplementary code points.</summary>
    internal const int LastSupplementaryCid = 0xDFFF;

    private const int MaxEntriesPerBlock = 100; // PDF limit for one begin/endbf* block

    private readonly Dictionary<int, int> _supplementary = new();

    /// <summary>Supplementary code points seen so far, mapped to their assigned CIDs.</summary>
    public IReadOnlyDictionary<int, int> SupplementaryCids => _supplementary;

    /// <summary>
    /// Appends the 4-hex-digit CIDs of <paramref name="text"/> to <paramref name="hex"/> and returns the glyph count.
    /// </summary>
    public int Encode(string text, StringBuilder hex)
    {
        int glyphs = 0;
        foreach (Rune rune in text.EnumerateRunes()) // invalid surrogates are yielded as U+FFFD
        {
            int value = rune.Value;
            int cid;
            if (value < 0x20 || value is >= 0x7F and <= 0x9F)
            {
                cid = 0x20;
            }
            else if (rune.IsBmp)
            {
                cid = value;
            }
            else if (!_supplementary.TryGetValue(value, out cid))
            {
                if (_supplementary.Count <= LastSupplementaryCid - FirstSupplementaryCid)
                {
                    cid = FirstSupplementaryCid + _supplementary.Count;
                    _supplementary.Add(value, cid);
                }
                else
                {
                    cid = 0xFFFD;
                }
            }

            hex.Append(cid.ToString("X4", CultureInfo.InvariantCulture));
            glyphs++;
        }
        return glyphs;
    }

    /// <summary>
    /// Builds the ToUnicode CMap: identity ranges for all BMP CIDs outside the surrogate block (256 per range,
    /// so each range varies only in its last byte), plus one <c>bfchar</c> per assigned supplementary CID.
    /// </summary>
    public string BuildToUnicodeCMap()
    {
        var sb = new StringBuilder(8192);
        sb.Append("/CIDInit /ProcSet findresource begin\n")
          .Append("12 dict begin\n")
          .Append("begincmap\n")
          .Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
          .Append("/CMapName /Adobe-Identity-UCS def\n")
          .Append("/CMapType 2 def\n")
          .Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        var ranges = new List<int>();
        for (int hi = 0; hi <= 0xFF; hi++)
        {
            if (hi is >= 0xD8 and <= 0xDF) continue;
            ranges.Add(hi);
        }
        for (int i = 0; i < ranges.Count; i += MaxEntriesPerBlock)
        {
            int n = Math.Min(MaxEntriesPerBlock, ranges.Count - i);
            sb.Append(n).Append(" beginbfrange\n");
            for (int k = 0; k < n; k++)
            {
                string hi = ranges[i + k].ToString("X2", CultureInfo.InvariantCulture);
                sb.Append('<').Append(hi).Append("00> <").Append(hi).Append("FF> <").Append(hi).Append("00>\n");
            }
            sb.Append("endbfrange\n");
        }

        var chars = _supplementary.OrderBy(kv => kv.Value).ToList();
        Span<char> utf16 = stackalloc char[2];
        for (int i = 0; i < chars.Count; i += MaxEntriesPerBlock)
        {
            int n = Math.Min(MaxEntriesPerBlock, chars.Count - i);
            sb.Append(n).Append(" beginbfchar\n");
            for (int k = 0; k < n; k++)
            {
                var (codePoint, cid) = chars[i + k];
                int len = new Rune(codePoint).EncodeToUtf16(utf16);
                sb.Append('<').Append(cid.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                for (int u = 0; u < len; u++)
                    sb.Append(((int)utf16[u]).ToString("X4", CultureInfo.InvariantCulture));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\n")
          .Append("CMapName currentdict /CMap defineresource pop\n")
          .Append("end\n")
          .Append("end\n");
        return sb.ToString();
    }
}
