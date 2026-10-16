using PaddleOcrNet.Models;
using PaddleOcrNet.Structure.Export;

namespace PaddleOcrNet.Structure;

/// <summary>Conservative multi-page assembly; never rewrites recognized text or silently joins tables.</summary>
public static class DocumentAssembler
{
    /// <summary>Build explicit relationships while preserving every supplied block and orphan line.
    /// Existing reading order is authoritative, including RTL pages; no left-to-right reordering is introduced.</summary>
    public static DocumentStructure Assemble(IEnumerable<DocumentPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var ordered = pages.OrderBy(p => p.PageNumber).ToArray();
        if (ordered.Any(p => p.PageNumber < 1 || p.Structure is null) ||
            ordered.Select(p => p.PageNumber).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Pages require unique positive page numbers and a structure result.", nameof(pages));

        ordered = ordered.Select(page =>
        {
            if (page.Structure.OrphanLines.Count == 0) return page;
            var orphans = new List<OcrLine>();
            foreach (var line in page.Structure.OrphanLines.Concat(page.OrphanLines))
                if (!orphans.Any(existing => SameEvidence(existing, line))) orphans.Add(line);
            return page with { OrphanLines = orphans.ToArray() };
        }).ToArray();

        var links = new List<DocumentRelationship>();
        foreach (var page in ordered)
        {
            var blocks = page.Structure.Blocks;
            var sequence = Enumerable.Range(0, blocks.Count).OrderBy(i => blocks[i].Order).ToArray();
            for (int i = 1; i < sequence.Length; i++)
                Link(DocumentRelationshipKind.Sequence, page, sequence[i - 1], page, sequence[i], "Existing page reading order", false);
            for (int i = 0; i < blocks.Count; i++)
            {
                // Only the smallest strict container is linked; overlapping siblings retain their identities.
                var container = Enumerable.Range(0, blocks.Count).Where(j => i != j && StrictlyContains(blocks[j].Bounds, blocks[i].Bounds))
                    .OrderBy(j => Area(blocks[j].Bounds)).Select(j => (int?)j).FirstOrDefault();
                if (container is { } parent)
                    Link(DocumentRelationshipKind.Contains, page, parent, page, i, "Strict bounding-box containment; semantic ownership unconfirmed", true);
            }
        }

        for (int p = 0; p < ordered.Length; p++)
        {
            var first = ordered[p];
            if (first.Structure.SourceWidth <= 0 || first.Structure.SourceHeight <= 0) continue;
            for (int q = p + 1; q < ordered.Length; q++)
            {
                var next = ordered[q];
                if (next.Structure.SourceWidth <= 0 || next.Structure.SourceHeight <= 0) continue;
                for (int i = 0; i < first.Structure.Blocks.Count; i++)
                for (int j = 0; j < next.Structure.Blocks.Count; j++)
                {
                    var a = first.Structure.Blocks[i];
                    var b = next.Structure.Blocks[j];
                    if (!Valid(a.Bounds) || !Valid(b.Bounds)) continue;
                    bool aligned = Math.Abs(a.Bounds.MinX / first.Structure.SourceWidth - b.Bounds.MinX / next.Structure.SourceWidth) <= 0.03 &&
                        Math.Abs(a.Bounds.MaxX / first.Structure.SourceWidth - b.Bounds.MaxX / next.Structure.SourceWidth) <= 0.03;
                    if (!aligned) continue;
                    if (a.Type == StructureBlockType.Header && b.Type == StructureBlockType.Header &&
                        a.Bounds.MaxY <= first.Structure.SourceHeight * 0.15 && b.Bounds.MaxY <= next.Structure.SourceHeight * 0.15 &&
                        Normalize(a.Text).Length > 0 && Normalize(a.Text) == Normalize(b.Text))
                        Link(DocumentRelationshipKind.RepeatedHeader, first, i, next, j,
                            "Header labels, identical normalized text, top 15%, horizontal edges within 3% of page width", true);

                    if (next.PageNumber != first.PageNumber + 1 || a.Type != StructureBlockType.Table || b.Type != StructureBlockType.Table ||
                        a.Bounds.MaxY < first.Structure.SourceHeight * 0.85 || b.Bounds.MinY > next.Structure.SourceHeight * 0.15) continue;
                    var ag = OoxmlHtmlTable.Parse(a.TableHtml);
                    var bg = OoxmlHtmlTable.Parse(b.TableHtml);
                    if (ag is null || bg is null || ag.ColumnCount < 2 || ag.ColumnCount != bg.ColumnCount) continue;
                    bool sameHeader = Enumerable.Range(0, ag.ColumnCount).All(c =>
                        !string.IsNullOrWhiteSpace(ag.Cells[0, c]?.Text) &&
                        Normalize(ag.Cells[0, c]?.Text) == Normalize(bg.Cells[0, c]?.Text));
                    Link(DocumentRelationshipKind.TableContinuationCandidate, first, i, next, j,
                        $"Adjacent pages, bottom/top 15%, aligned horizontal edges, {ag.ColumnCount} columns; " +
                        (sameHeader ? "identical first-row text" : "first-row text differs or contains merged cells"), true);
                }
            }
        }
        return new DocumentStructure { Pages = ordered, Relationships = links.ToArray() };

        void Link(DocumentRelationshipKind kind, DocumentPage a, int ai, DocumentPage b, int bi, string evidence, bool review)
            => links.Add(new DocumentRelationship { Kind = kind, From = new(a.PageNumber, ai), To = new(b.PageNumber, bi),
                Evidence = evidence, RequiresReview = review });
    }

    private static string Normalize(string? text) => string.Join(" ", (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static bool SameEvidence(OcrLine a, OcrLine b) => (a with
        { BoundingPolygon = b.BoundingPolygon, Words = b.Words }) == b &&
        a.BoundingPolygon.SequenceEqual(b.BoundingPolygon) && a.Words.Count == b.Words.Count &&
        a.Words.Zip(b.Words).All(pair =>
            (pair.First with { BoundingPolygon = pair.Second.BoundingPolygon }) == pair.Second &&
            pair.First.BoundingPolygon.SequenceEqual(pair.Second.BoundingPolygon));
    private static double Area(OcrBoundingBox b) => b.Width * b.Height;
    private static bool Valid(OcrBoundingBox b) => double.IsFinite(b.MinX) && double.IsFinite(b.MinY) &&
        double.IsFinite(b.MaxX) && double.IsFinite(b.MaxY) && b.Width > 0 && b.Height > 0;
    private static bool StrictlyContains(OcrBoundingBox a, OcrBoundingBox b) => Valid(a) && Valid(b) && Area(a) > Area(b) &&
        a.MinX <= b.MinX && a.MinY <= b.MinY && a.MaxX >= b.MaxX && a.MaxY >= b.MaxY;
}
