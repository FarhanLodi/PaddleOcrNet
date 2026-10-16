using System.Text;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Pdf.Internal;

internal static class HybridTextLayer
{
    internal static bool IsReliable(OcrLine line, int width, int height)
    {
        var box = line.BoundingBox;
        if (!double.IsFinite(box.MinX) || !double.IsFinite(box.MinY) ||
            !double.IsFinite(box.MaxX) || !double.IsFinite(box.MaxY) ||
            box.Width <= 0 || box.Height <= 0 || box.MaxX <= 0 || box.MaxY <= 0 ||
            box.MinX >= width || box.MinY >= height) return false;
        if (!IsWellFormed(line.Text)) return false;
        var chars = line.Text.EnumerateRunes().Where(c => !Rune.IsWhiteSpace(c)).ToArray();
        return chars.Length > 0 && !chars.Any(c => c.Value == 0xFFFD || Rune.IsControl(c)
            || Rune.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.PrivateUse)
            && chars.Any(Rune.IsLetterOrDigit);
    }

    internal static IReadOnlyList<PdfLineEvidence> Fuse(IReadOnlyList<OcrLine> native,
        IReadOnlyList<OcrLine> visual, int width, int height)
    {
        var selected = new List<PdfLineEvidence>();
        foreach (var line in native.Where(l => IsReliable(l, width, height)))
        {
            // Duplicate invisible layers should not produce duplicated searchable text.
            int index = selected.FindIndex(e => SameRegion(e.Selected, line));
            if (index >= 0)
            {
                var prior = selected[index];
                if (Normalize(prior.Selected.Text) != Normalize(line.Text))
                    selected[index] = AddAlternative(prior, line, PdfPageSource.EmbeddedText);
                continue;
            }
            selected.Add(new PdfLineEvidence { Selected = line, Source = PdfPageSource.EmbeddedText });
        }
        foreach (var line in visual)
        {
            int index = selected.FindIndex(e => e.Source == PdfPageSource.EmbeddedText && SameRegion(e.Selected, line));
            if (index >= 0)
            {
                selected[index] = AddAlternative(selected[index], line, PdfPageSource.Ocr);
            }
            else if (!selected.Any(e => SameRegion(e.Selected, line) && Normalize(e.Selected.Text) == Normalize(line.Text)))
                selected.Add(new PdfLineEvidence { Selected = line, Source = PdfPageSource.Ocr });
        }
        return selected.OrderBy(e => e.Selected.BoundingBox.MinY).ThenBy(e => e.Selected.BoundingBox.MinX).ToArray();
    }

    private static PdfLineEvidence AddAlternative(PdfLineEvidence prior, OcrLine line, PdfPageSource source)
        => prior with
        {
            Alternatives = prior.Alternatives.Append(line).ToArray(),
            AlternativeSources = prior.AlternativeSources.Append(source).ToArray(),
            HasConflict = prior.HasConflict || Normalize(prior.Selected.Text) != Normalize(line.Text),
        };

    private static string Normalize(string text)
    {
        // Malformed custom visual results must remain evidence without crashing reconciliation.
        // EnumerateRunes substitutes malformed UTF-16 with U+FFFD before NFC normalization.
        if (!IsWellFormed(text))
        {
            var repaired = new StringBuilder(text.Length);
            foreach (var rune in text.EnumerateRunes()) repaired.Append(rune.ToString());
            text = repaired.ToString();
        }
        return string.Join(" ", text.Normalize(NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsWellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
            i++;
        }
        return true;
    }

    private static bool SameRegion(OcrLine a, OcrLine b)
    {
        var x = a.BoundingBox;
        var y = b.BoundingBox;
        double intersection = Math.Max(0, Math.Min(x.MaxX, y.MaxX) - Math.Max(x.MinX, y.MinX)) *
            Math.Max(0, Math.Min(x.MaxY, y.MaxY) - Math.Max(x.MinY, y.MinY));
        double small = Math.Min(x.Width * x.Height, y.Width * y.Height);
        double large = Math.Max(x.Width * x.Height, y.Width * y.Height);
        // Require comparable extents: a small native header must not suppress a much larger OCR region.
        return small > 0 && intersection / small >= 0.7 && intersection / large >= 0.5;
    }

    internal static double UnionCoverage(IReadOnlyList<OcrLine> lines, int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        var boxes = lines.Where(l => IsReliable(l, width, height)).Select(l => new OcrBoundingBox(
            Math.Clamp(l.BoundingBox.MinX, 0, width), Math.Clamp(l.BoundingBox.MinY, 0, height),
            Math.Clamp(l.BoundingBox.MaxX, 0, width), Math.Clamp(l.BoundingBox.MaxY, 0, height))).ToArray();
        var xs = boxes.SelectMany(b => new[] { b.MinX, b.MaxX }).Distinct().Order().ToArray();
        double area = 0;
        for (int i = 1; i < xs.Length; i++)
        {
            var spans = boxes.Where(b => b.MinX < xs[i] && b.MaxX > xs[i - 1]).OrderBy(b => b.MinY);
            double covered = 0, end = 0;
            foreach (var b in spans)
            {
                covered += Math.Max(0, b.MaxY - Math.Max(end, b.MinY));
                end = Math.Max(end, b.MaxY);
            }
            area += (xs[i] - xs[i - 1]) * covered;
        }
        return area / ((double)width * height);
    }
}
