using System.Globalization;
using System.Text;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Extraction.Internal;

/// <summary>
/// Shared text-metric and geometry helpers for the extraction features: terminal-style display widths
/// (CJK / full-width characters count as two columns, combining marks as zero), proportional sub-range
/// boxes over a line (the same estimate the exporters use for word boxes), RTL detection, and
/// skew-tolerant grouping of line boxes into visual rows.
/// </summary>
internal static class TextGeometry
{
    /// <summary>Display width of <paramref name="text"/> in character cells.</summary>
    public static int DisplayWidth(string text) => DisplayWidth(text.AsSpan());

    /// <summary>Display width of <paramref name="text"/> in character cells.</summary>
    public static int DisplayWidth(ReadOnlySpan<char> text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += RuneWidth(rune);
        }
        return width;
    }

    /// <summary>Number of non-whitespace Unicode scalar values in <paramref name="text"/>.</summary>
    public static int CountCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune)) count++;
        }
        return count;
    }

    private static int RuneWidth(Rune rune)
    {
        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.Format:
            case UnicodeCategory.Control:
                return 0;
        }
        return IsWide(rune.Value) ? 2 : 1;
    }

    private static bool IsWide(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F)       // Hangul Jamo
        || (cp >= 0x2E80 && cp <= 0x303E)    // CJK radicals, punctuation
        || (cp >= 0x3041 && cp <= 0x33FF)    // Hiragana, Katakana, CJK compatibility
        || (cp >= 0x3400 && cp <= 0x4DBF)    // CJK Extension A
        || (cp >= 0x4E00 && cp <= 0x9FFF)    // CJK Unified Ideographs
        || (cp >= 0xA000 && cp <= 0xA4CF)    // Yi
        || (cp >= 0xAC00 && cp <= 0xD7A3)    // Hangul syllables
        || (cp >= 0xF900 && cp <= 0xFAFF)    // CJK compatibility ideographs
        || (cp >= 0xFE30 && cp <= 0xFE4F)    // CJK compatibility forms
        || (cp >= 0xFF00 && cp <= 0xFF60)    // Full-width forms
        || (cp >= 0xFFE0 && cp <= 0xFFE6)    // Full-width signs
        || (cp >= 0x1F300 && cp <= 0x1F64F)  // Emoji
        || (cp >= 0x1F900 && cp <= 0x1F9FF)
        || (cp >= 0x20000 && cp <= 0x3FFFD); // CJK Extensions B+

    /// <summary>
    /// The line's axis-aligned box, falling back to the polygon's extents when the box was not populated.
    /// </summary>
    public static OcrBoundingBox BoxOf(OcrLine line)
    {
        var box = line.BoundingBox;
        if (box.IsEmpty && line.BoundingPolygon.Count > 0)
        {
            box = OcrBoundingBox.FromPoints(line.BoundingPolygon);
        }
        return box;
    }

    /// <summary>
    /// Estimates the box of the character range [<paramref name="start"/>, <paramref name="start"/> +
    /// <paramref name="length"/>) by allocating the line width proportionally to display width — the model
    /// only yields line-level geometry, so this mirrors how the hOCR/ALTO/TSV exporters estimate word boxes.
    /// </summary>
    public static OcrBoundingBox EstimateRangeBox(OcrLine line, int start, int length)
    {
        var box = BoxOf(line);
        string text = line.Text;
        int total = DisplayWidth(text);
        if (total <= 0 || box.Width <= 0)
        {
            return box;
        }

        double unit = box.Width / total;
        double minX = box.MinX + DisplayWidth(text.AsSpan(0, start)) * unit;
        double maxX = minX + DisplayWidth(text.AsSpan(start, length)) * unit;
        return new OcrBoundingBox(minX, box.MinY, maxX, box.MaxY);
    }

    /// <summary>True when strong right-to-left characters outnumber strong left-to-right letters.</summary>
    public static bool IsPredominantlyRtl(string text)
    {
        int rtl = 0, ltr = 0;
        foreach (char c in text)
        {
            if (IsRtlChar(c)) rtl++;
            else if (char.IsLetter(c)) ltr++;
        }
        return rtl > ltr;
    }

    private static bool IsRtlChar(char c) =>
        (c >= '֐' && c <= '׿')     // Hebrew
        || (c >= '؀' && c <= 'ۿ')  // Arabic
        || (c >= 'ݐ' && c <= 'ݿ')  // Arabic Supplement
        || (c >= 'ࢠ' && c <= 'ࣿ')  // Arabic Extended-A
        || (c >= 'יִ' && c <= '﷿')  // Hebrew / Arabic presentation forms A
        || (c >= 'ﹰ' && c <= '﻿'); // Arabic presentation forms B

    /// <summary>
    /// Groups boxes into visual rows. Boxes are visited top-down; each joins the existing row containing a
    /// member it overlaps vertically by at least <paramref name="overlapThreshold"/> of the smaller height.
    /// Comparing against individual members (not the row's union) tolerates gentle page skew, and a box that
    /// substantially overlaps a member horizontally is never merged into that row (it is a stacked line).
    /// Rows are returned top-to-bottom, each ordered left-to-right, as indices into <paramref name="boxes"/>.
    /// </summary>
    public static List<List<int>> GroupRows(IReadOnlyList<OcrBoundingBox> boxes, double overlapThreshold)
    {
        var order = Enumerable.Range(0, boxes.Count)
            .OrderBy(i => boxes[i].MinY)
            .ThenBy(i => boxes[i].MinX)
            .ToList();

        var rows = new List<List<int>>();
        foreach (int i in order)
        {
            var b = boxes[i];
            int bestRow = -1;
            double bestRatio = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                double rowRatio = 0;
                bool stacked = false;
                foreach (int j in rows[r])
                {
                    var o = boxes[j];
                    double minW = Math.Min(b.Width, o.Width);
                    double hOverlap = Math.Min(b.MaxX, o.MaxX) - Math.Max(b.MinX, o.MinX);
                    if (minW > 0 && hOverlap > 0.5 * minW)
                    {
                        stacked = true;
                        break;
                    }

                    double minH = Math.Min(b.Height, o.Height);
                    if (minH <= 0) continue;
                    double vOverlap = Math.Min(b.MaxY, o.MaxY) - Math.Max(b.MinY, o.MinY);
                    rowRatio = Math.Max(rowRatio, vOverlap / minH);
                }

                if (!stacked && rowRatio >= overlapThreshold && rowRatio > bestRatio)
                {
                    bestRatio = rowRatio;
                    bestRow = r;
                }
            }

            if (bestRow >= 0) rows[bestRow].Add(i);
            else rows.Add(new List<int> { i });
        }

        foreach (var row in rows)
        {
            row.Sort((x, y) => boxes[x].MinX.CompareTo(boxes[y].MinX));
        }
        rows.Sort((x, y) => MeanCenterY(boxes, x).CompareTo(MeanCenterY(boxes, y)));
        return rows;
    }

    private static double MeanCenterY(IReadOnlyList<OcrBoundingBox> boxes, List<int> row)
    {
        double sum = 0;
        foreach (int i in row) sum += boxes[i].CenterY;
        return sum / row.Count;
    }

    /// <summary>Median of <paramref name="values"/> (sorted in place); NaN when empty.</summary>
    public static double Median(List<double> values)
    {
        if (values.Count == 0) return double.NaN;
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }

    /// <summary>
    /// Mean line confidence weighted by each line's non-whitespace character count (a line with no
    /// characters weighs 1). Returns 0 for no lines.
    /// </summary>
    public static double WeightedConfidence(IReadOnlyList<OcrLine> lines)
    {
        double sum = 0, weight = 0;
        foreach (var line in lines)
        {
            double w = Math.Max(1, CountCharacters(line.Text));
            sum += line.Confidence * w;
            weight += w;
        }
        return weight > 0 ? sum / weight : 0;
    }

    /// <summary>
    /// Joins lines in reading order: rows top-to-bottom separated by newlines, row members joined by a
    /// space (right-to-left rows are joined right-to-left). Lines without geometry keep their given order.
    /// </summary>
    public static string JoinReadingOrder(IReadOnlyList<OcrLine> lines)
    {
        var texts = new List<string>(lines.Count);
        var boxes = new List<OcrBoundingBox>(lines.Count);
        foreach (var line in lines)
        {
            string text = line.Text.Trim();
            if (text.Length == 0) continue;
            texts.Add(text);
            boxes.Add(BoxOf(line));
        }

        if (texts.Count == 0) return string.Empty;
        if (boxes.All(b => b.IsEmpty)) return string.Join('\n', texts);

        var sb = new StringBuilder();
        foreach (var row in GroupRows(boxes, 0.5))
        {
            if (sb.Length > 0) sb.Append('\n');
            IEnumerable<int> members = row;
            if (row.Count > 1 && row.All(i => IsPredominantlyRtl(texts[i])))
            {
                members = Enumerable.Reverse(row);
            }
            sb.AppendJoin(' ', members.Select(i => texts[i]));
        }
        return sb.ToString();
    }
}
