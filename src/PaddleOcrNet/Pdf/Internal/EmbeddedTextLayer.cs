using System.Globalization;
using System.Text;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;

namespace PaddleOcrNet.Pdf.Internal;

/// <summary>
/// Turns a PDF page's embedded characters into <see cref="OcrLine"/>s, and decides whether the text layer is
/// trustworthy enough to use instead of OCR. Pure managed code; PDFium access lives in <see cref="PdfRasterizer"/>.
/// </summary>
internal static class EmbeddedTextLayer
{
    /// <summary>Minimum number of non-whitespace characters for the text layer to be used.</summary>
    internal const int MinCharacters = 20;

    /// <summary>Maximum share of replacement, control or private-use characters (exclusive).</summary>
    internal const double MaxInvalidRatio = 0.05;

    /// <summary>Minimum share of letters or digits among non-whitespace characters.</summary>
    internal const double MinLetterOrDigitRatio = 0.60;

    /// <summary>In <see cref="PdfTextLayerMode.Auto"/>, the minimum share of the page area the character boxes must cover.</summary>
    internal const double MinAutoCoverage = 0.02;

    /// <summary>A horizontal gap wider than this many em heights starts a new word.</summary>
    internal const double WordGapFactor = 0.3;

    /// <summary>
    /// A gap must also exceed this multiple of the line's median letter gap to start a word. That keeps letter-spaced
    /// (tracked) text, whose letter gaps exceed 0.3 em, from being split into single letters.
    /// </summary>
    internal const double TrackingGapFactor = 1.5;

    /// <summary>Minimum number of letter gaps on a line before its median spacing is trusted.</summary>
    internal const int MinTrackingSamples = 3;

    /// <summary>A horizontal gap wider than this many em heights starts a new line, as OCR detection splits columns.</summary>
    internal const double ColumnGapFactor = 2.0;

    /// <summary>Minimum vertical overlap, relative to the shorter box, for a character to join the current line.</summary>
    internal const double MinVerticalOverlap = 0.5;

    /// <summary>Maximum baseline shift, in em heights, for a character to join the current line.</summary>
    internal const double MaxBaselineShift = 0.5;

    /// <summary>
    /// Decides whether a page's embedded text should be used for the given mode.
    /// </summary>
    public static bool ShouldUse(IReadOnlyList<PdfTextChar> chars, PdfTextLayerMode mode, int pageWidth, int pageHeight)
        => mode switch
        {
            PdfTextLayerMode.PreferEmbedded => PassesQualityGate(chars),
            PdfTextLayerMode.Auto => PassesQualityGate(chars) && CoverageRatio(chars, pageWidth, pageHeight) >= MinAutoCoverage,
            _ => false,
        };

    /// <summary>
    /// The quality gate: at least <see cref="MinCharacters"/> non-whitespace characters, fewer than
    /// <see cref="MaxInvalidRatio"/> of them invalid, and at least <see cref="MinLetterOrDigitRatio"/> letters or
    /// digits. A broken ToUnicode map typically yields U+FFFD, controls or private-use code points and fails here.
    /// </summary>
    public static bool PassesQualityGate(IReadOnlyList<PdfTextChar> chars)
    {
        int total = 0, invalid = 0, lettersOrDigits = 0;
        foreach (var c in chars)
        {
            if (char.IsWhiteSpace(c.Value)) continue;
            total++;
            if (IsInvalid(c.Value)) invalid++;
            else if (char.IsLetterOrDigit(c.Value)) lettersOrDigits++;
        }

        return total >= MinCharacters
            && invalid < total * MaxInvalidRatio
            && lettersOrDigits >= total * MinLetterOrDigitRatio;
    }

    /// <summary>
    /// The share of the page area covered by non-whitespace character boxes (overlaps are not subtracted).
    /// </summary>
    public static double CoverageRatio(IReadOnlyList<PdfTextChar> chars, int pageWidth, int pageHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0) return 0;
        double area = 0;
        foreach (var c in chars)
        {
            if (char.IsWhiteSpace(c.Value)) continue;
            area += Math.Max(0, c.Right - c.Left) * Math.Max(0, c.Bottom - c.Top);
        }
        return area / ((double)pageWidth * pageHeight);
    }

    /// <summary>
    /// Groups characters (in content-stream order) into lines by vertical overlap and baseline, starting a new line
    /// when the text jumps backwards or across a gap wider than <see cref="ColumnGapFactor"/> em. Within a line, a
    /// word starts at explicit whitespace, or at a gap wider than both <see cref="WordGapFactor"/> em and
    /// <see cref="TrackingGapFactor"/> times the line's median letter gap.
    /// </summary>
    public static List<OcrLine> BuildLines(IReadOnlyList<PdfTextChar> chars)
    {
        var lines = new List<OcrLine>();
        var glyphs = new List<(PdfTextChar Char, bool SpaceBefore)>();
        double lineTop = 0, lineBottom = 0;
        bool pendingSpace = false;

        foreach (var c in chars)
        {
            if (char.IsWhiteSpace(c.Value) || char.IsControl(c.Value))
            {
                // PDFium reports both real spaces and generated CR/LF here; geometry decides line breaks, so any
                // whitespace only marks a word boundary.
                if (glyphs.Count > 0) pendingSpace = true;
                continue;
            }

            if (glyphs.Count > 0 && !JoinsLine(c, glyphs[^1].Char, lineTop, lineBottom))
            {
                AddLine(lines, glyphs);
                glyphs.Clear();
                pendingSpace = false;
            }

            if (glyphs.Count == 0)
            {
                lineTop = c.Top;
                lineBottom = c.Bottom;
            }
            else
            {
                lineTop = Math.Min(lineTop, c.Top);
                lineBottom = Math.Max(lineBottom, c.Bottom);
            }

            glyphs.Add((c, pendingSpace));
            pendingSpace = false;
        }

        AddLine(lines, glyphs);
        return lines;
    }

    /// <summary>
    /// Builds the <see cref="OcrResult"/> for a page read from its embedded text layer.
    /// </summary>
    public static OcrResult CreateResult(IReadOnlyList<OcrLine> lines, int pixelWidth, int pixelHeight, IReadOnlyList<string> languages, TimeSpan duration)
        => new()
        {
            FullText = PaddleOcrService.BuildFullText(lines, TextGrouping.Line),
            Lines = lines,
            Languages = languages,
            Duration = duration,
            SourceWidth = pixelWidth,
            SourceHeight = pixelHeight,
        };

    /// <summary>
    /// True for a replacement, control (non-whitespace) or private-use character.
    /// </summary>
    internal static bool IsInvalid(char ch)
        => ch == '�'
        || (char.IsControl(ch) && !char.IsWhiteSpace(ch))
        || char.GetUnicodeCategory(ch) == UnicodeCategory.PrivateUse;

    private static void AddLine(List<OcrLine> lines, List<(PdfTextChar Char, bool SpaceBefore)> glyphs)
    {
        if (glyphs.Count == 0) return;

        var letterGaps = new List<double>();
        for (int i = 1; i < glyphs.Count; i++)
        {
            if (!glyphs[i].SpaceBefore)
                letterGaps.Add(glyphs[i].Char.Left - glyphs[i - 1].Char.Right);
        }
        double typicalGap = letterGaps.Count >= MinTrackingSamples ? Median(letterGaps) : 0;

        var text = new StringBuilder(glyphs.Count + 8);
        var first = glyphs[0].Char;
        double left = first.Left, top = first.Top, right = first.Right, bottom = first.Bottom;
        for (int i = 0; i < glyphs.Count; i++)
        {
            var (c, spaceBefore) = glyphs[i];
            if (i > 0)
            {
                var prev = glyphs[i - 1].Char;
                double em = Math.Max(c.EmHeight, prev.EmHeight);
                double threshold = Math.Max(WordGapFactor * em, TrackingGapFactor * typicalGap);
                if (spaceBefore || c.Left - prev.Right > threshold)
                    text.Append(' ');
            }

            text.Append(c.Value);
            left = Math.Min(left, c.Left);
            top = Math.Min(top, c.Top);
            right = Math.Max(right, c.Right);
            bottom = Math.Max(bottom, c.Bottom);
        }

        lines.Add(new OcrLine
        {
            Text = text.ToString(),
            Confidence = 1.0,
            BoundingPolygon = new[]
            {
                new OcrPoint(left, top), new OcrPoint(right, top),
                new OcrPoint(right, bottom), new OcrPoint(left, bottom),
            },
            BoundingBox = new OcrBoundingBox(left, top, right, bottom),
        });
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }

    private static bool JoinsLine(in PdfTextChar c, in PdfTextChar prev, double lineTop, double lineBottom)
    {
        double em = Math.Max(c.EmHeight, prev.EmHeight);
        double gap = c.Left - prev.Right;
        if (gap < -em || gap > ColumnGapFactor * em) return false;

        // A glyph nested in the line band (or containing it) always joins: punctuation, quotes, a capital after a quote.
        bool nested = (c.Top >= lineTop && c.Bottom <= lineBottom) || (c.Top <= lineTop && c.Bottom >= lineBottom);
        if (nested) return true;

        double lineHeight = Math.Max(1.0, lineBottom - lineTop);
        double charHeight = Math.Max(1.0, c.Bottom - c.Top);
        double overlap = Math.Min(lineBottom, c.Bottom) - Math.Max(lineTop, c.Top);
        bool overlaps = overlap >= MinVerticalOverlap * Math.Min(lineHeight, charHeight);
        bool sameBaseline = Math.Abs(c.Bottom - prev.Bottom) <= MaxBaselineShift * em
            || Math.Abs(c.Bottom - lineBottom) <= MaxBaselineShift * em;
        return overlaps && sameBaseline;
    }
}
