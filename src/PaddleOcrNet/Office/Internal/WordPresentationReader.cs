using System.Text;
using System.Xml.Linq;

namespace PaddleOcrNet.Office.Internal;

internal static class WordPresentationReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";

    internal static IReadOnlyList<OfficeDocumentSection> ReadWord(OfficePackage package, string mainPart, CancellationToken ct)
    {
        var document = package.ReadXml(mainPart);
        NormalizeStrictNamespaces(document.Root);
        if (document.Root?.Name != W + "document") throw new InvalidDataException("Unsupported Word document root or namespace.");
        var body = document.Root?.Element(W + "body") ?? throw new InvalidDataException("The Word main part has no document body.");
        SelectCompatibilityBranches(body);
        var sections = new List<OfficeDocumentSection>();
        AddSection(body, mainPart, "Document body");
        var visited = new HashSet<string>(StringComparer.Ordinal) { mainPart };
        int headers = 0, footers = 0;
        foreach (var reference in body.Descendants().Where(e => e.Name == W + "headerReference" || e.Name == W + "footerReference"))
        {
            ct.ThrowIfCancellationRequested();
            bool header = reference.Name == W + "headerReference";
            var target = Resolve(package, mainPart, (string?)reference.Attribute(R + "id"), header ? "header" : "footer");
            if (target is null || !visited.Add(target)) continue;
            string variant = (string?)reference.Attribute(W + "type") ?? "default";
            var root = package.ReadXml(target).Root;
            NormalizeStrictNamespaces(root);
            if (root?.Name != W + (header ? "hdr" : "ftr")) throw new InvalidDataException("Invalid Word header/footer root or namespace.");
            AddSection(root, target,
                header ? $"Header {++headers} ({variant})" : $"Footer {++footers} ({variant})");
        }
        foreach (var relation in package.Relationships(mainPart))
            if (relation.Type.EndsWith("/footnotes", StringComparison.Ordinal) || relation.Type.EndsWith("/endnotes", StringComparison.Ordinal))
                package.Warn("Word footnotes/endnotes are not extracted; their text is outside the returned body/header/footer sections.");
        return sections;

        void AddSection(XElement content, string part, string name)
        {
            ct.ThrowIfCancellationRequested();
            SelectCompatibilityBranches(content);
            package.AddSection();
            var blocks = new List<OfficeContentBlock>();
            ReadWordChildren(package, content, part, blocks, ct);
            sections.Add(new OfficeDocumentSection { Index = sections.Count + 1, Name = name, SourcePart = part, Blocks = blocks });
        }
    }

    private static void ReadWordChildren(OfficePackage package, XElement parent, string part, List<OfficeContentBlock> blocks, CancellationToken ct, int depth = 0)
    {
        CheckDepth(depth);
        foreach (var child in parent.Elements())
        {
            ct.ThrowIfCancellationRequested();
            if (child.Name == W + "del" || child.Name == W + "moveFrom") continue;
            if (child.Name == W + "p")
            {
                string text = WordText(package, child);
                if (text.Length > 0) Add(package, blocks, new OfficeContentBlock { Kind = OfficeContentKind.Paragraph, Text = text });
                ReadImages(package, child, part, blocks, ct);
                WarnObjects(package, child);
            }
            else if (child.Name == W + "tbl")
                ReadWordTable(package, child, part, blocks, ct);
            else if (child.Name == W + "sdt")
            {
                if (child.Element(W + "sdtContent") is { } content) ReadWordChildren(package, content, part, blocks, ct, depth + 1);
            }
            else if (child.Name == W + "customXml" || child.Name == W + "ins" || child.Name == W + "moveTo")
                ReadWordChildren(package, child, part, blocks, ct, depth + 1);
            else if (child.Name == W + "altChunk")
                package.Warn("Word altChunk content is not imported or executed.");
        }
    }

    private static void ReadWordTable(OfficePackage package, XElement table, string part, List<OfficeContentBlock> blocks, CancellationToken ct, int depth = 0)
    {
        CheckDepth(depth);
        var cells = new List<OfficeTableCell>();
        var previousMerges = new Dictionary<int, int>();
        int row = 0;
        foreach (var tr in table.Elements(W + "tr"))
        {
            ct.ThrowIfCancellationRequested();
            if (tr.Element(W + "trPr")?.Element(W + "del") is not null) continue;
            row++;
            int column = checked(1 + Nonnegative((string?)tr.Element(W + "trPr")?.Element(W + "gridBefore")?.Attribute(W + "val")));
            var currentMerges = new Dictionary<int, int>();
            foreach (var tc in tr.Elements(W + "tc"))
            {
                ct.ThrowIfCancellationRequested();
                package.AddBlock(); // Cells are output units too; a single table cannot bypass the global output budget.
                int span = Positive((string?)tc.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val"));
                var merge = tc.Element(W + "tcPr")?.Element(W + "vMerge");
                bool continuation = merge is not null && (string?)merge.Attribute(W + "val") != "restart";
                var paragraphs = tc.Descendants(W + "p").Where(p => p.Ancestors(W + "tbl").FirstOrDefault() == table && !p.Ancestors(W + "p").Any() && !Deleted(p));
                string text = JoinParagraphs(package, paragraphs.Select(p => WordText(package, p)));
                if (continuation && previousMerges.TryGetValue(column, out int anchor) && cells[anchor].ColumnSpan == span)
                {
                    var prior = cells[anchor];
                    if (text.Length > 0 && prior.Text.Length > 0) package.AddText("\n");
                    cells[anchor] = prior with { RowSpan = checked(prior.RowSpan + 1),
                        Text = text.Length == 0 ? prior.Text : prior.Text.Length == 0 ? text : prior.Text + "\n" + text };
                    currentMerges[column] = anchor;
                }
                else
                {
                    if (continuation) package.Warn("Word table has an unmatched vertical merge; its cell is preserved separately.");
                    cells.Add(new OfficeTableCell { Row = row, Column = column, RowSpan = 1, ColumnSpan = span, Text = text });
                    if (merge is not null) currentMerges[column] = cells.Count - 1;
                }
                if (tc.Element(W + "tcPr")?.Element(W + "hMerge") is not null)
                    package.Warn("Legacy Word hMerge is retained as separate cells; gridSpan is the supported horizontal merge representation.");
                if (span > 16385 - column) throw new InvalidDataException("Word table exceeds the supported column limit.");
                column += span;
            }
            previousMerges = currentMerges;
        }
        Add(package, blocks, new OfficeContentBlock { Kind = OfficeContentKind.Table, Cells = cells, Text = TableText(package, cells) });
        // Text belongs to table cells, not duplicate paragraph blocks; image occurrences remain independently OCR-able.
        foreach (var child in table.Descendants().Where(e => e.Name == W + "p" && e.Ancestors(W + "tbl").FirstOrDefault() == table && !e.Ancestors(W + "p").Any()))
        {
            ReadImages(package, child, part, blocks, ct);
            WarnObjects(package, child);
        }
        foreach (var nested in table.Descendants(W + "tbl").Where(t => t.Ancestors(W + "tbl").FirstOrDefault() == table && !Deleted(t)))
            ReadWordTable(package, nested, part, blocks, ct, depth + 1);
    }

    private static string WordText(OfficePackage package, XElement paragraph)
    {
        var text = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (Deleted(node)) continue;
            string? value = node.Name == W + "t" ? node.Value : node.Name == W + "tab" ? "\t"
                : node.Name == W + "br" || node.Name == W + "cr" ? "\n"
                : node.Name == W + "noBreakHyphen" ? "\u2011" : node.Name == W + "softHyphen" ? "\u00ad" : null;
            if (value is not null) { package.AddText(value); text.Append(value); }
        }
        return text.ToString();
    }

    internal static IReadOnlyList<OfficeDocumentSection> ReadPresentation(OfficePackage package, string mainPart, CancellationToken ct)
    {
        var document = package.ReadXml(mainPart);
        NormalizeStrictNamespaces(document.Root);
        if (document.Root?.Name != P + "presentation") throw new InvalidDataException("Unsupported presentation root or namespace.");
        var ids = document.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? Enumerable.Empty<XElement>();
        var sections = new List<OfficeDocumentSection>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            string? part = Resolve(package, mainPart, (string?)id.Attribute(R + "id"), "slide");
            if (part is null) continue;
            package.AddSection();
            var slide = package.ReadXml(part);
            NormalizeStrictNamespaces(slide.Root);
            if (slide.Root?.Name != P + "sld") throw new InvalidDataException("Invalid presentation slide root or namespace.");
            if (slide.Root is { } slideRoot) SelectCompatibilityBranches(slideRoot);
            var blocks = new List<OfficeContentBlock>();
            if (slide.Root?.Element(P + "cSld")?.Element(P + "spTree") is { } tree)
                ReadShapes(package, tree, part, blocks, ct);
            else package.Warn("Presentation slide has no supported shape tree.");
            foreach (var relation in package.Relationships(part))
            {
                if (relation.Type.EndsWith("/notesSlide", StringComparison.Ordinal))
                    package.Warn("Presentation speaker notes are not extracted.");
                if (relation.Type.EndsWith("/slideLayout", StringComparison.Ordinal))
                    package.Warn("Presentation layout/master-only text and images are not included; extraction reads explicit slide shapes.");
            }
            sections.Add(new OfficeDocumentSection { Index = sections.Count + 1, Name = $"Slide {sections.Count + 1}", SourcePart = part, Blocks = blocks });
        }
        return sections;
    }

    private static void ReadShapes(OfficePackage package, XElement tree, string part, List<OfficeContentBlock> blocks, CancellationToken ct, int depth = 0)
    {
        CheckDepth(depth);
        foreach (var shape in tree.Elements())
        {
            ct.ThrowIfCancellationRequested();
            if (shape.Name == P + "grpSp") { ReadShapes(package, shape, part, blocks, ct, depth + 1); continue; }
            if (shape.Name == P + "sp")
            {
                foreach (var paragraph in shape.Element(P + "txBody")?.Elements(A + "p") ?? Enumerable.Empty<XElement>())
                {
                    string text = DrawingText(package, paragraph);
                    if (text.Length > 0) Add(package, blocks, new OfficeContentBlock { Kind = OfficeContentKind.Paragraph, Text = text });
                }
            }
            if (shape.Name == P + "graphicFrame")
            {
                foreach (var table in shape.Descendants(A + "tbl")) ReadDrawingTable(package, table, blocks, ct);
                if (!shape.Descendants(A + "tbl").Any())
                    package.Warn("Presentation chart, diagram or other non-table graphic frame is not rendered or extracted.");
            }
            ReadImages(package, shape, part, blocks, ct);
            WarnObjects(package, shape);
        }
    }

    private static void ReadDrawingTable(OfficePackage package, XElement table, List<OfficeContentBlock> blocks, CancellationToken ct)
    {
        var cells = new List<OfficeTableCell>();
        var rows = table.Elements(A + "tr").ToArray();
        int columns = table.Element(A + "tblGrid")?.Elements(A + "gridCol").Count()
            ?? rows.Select(r => r.Elements(A + "tc").Count()).DefaultIfEmpty().Max();
        int row = 0;
        foreach (var tr in rows)
        {
            row++;
            int column = 0;
            foreach (var cell in tr.Elements(A + "tc"))
            {
                ct.ThrowIfCancellationRequested();
                column++;
                package.AddBlock();
                bool merged = IsTrue((string?)cell.Attribute("hMerge")) || IsTrue((string?)cell.Attribute("vMerge"));
                string text = JoinParagraphs(package, cell.Element(A + "txBody")?.Elements(A + "p").Select(p => DrawingText(package, p)) ?? Enumerable.Empty<string>());
                if (merged)
                {
                    if (text.Length > 0) package.Warn("Presentation merged-cell continuation contains text; retained as a separate cell to avoid losing evidence.");
                    else continue;
                }
                int rowSpan = Positive((string?)cell.Attribute("rowSpan")), columnSpan = Positive((string?)cell.Attribute("gridSpan"));
                if (rowSpan > rows.Length - row + 1 || columnSpan > columns - column + 1)
                    throw new InvalidDataException("Presentation cell span extends outside the table grid.");
                cells.Add(new OfficeTableCell { Row = row, Column = column, RowSpan = rowSpan, ColumnSpan = columnSpan, Text = text });
            }
        }
        Add(package, blocks, new OfficeContentBlock { Kind = OfficeContentKind.Table, Cells = cells, Text = TableText(package, cells) });
    }

    private static string DrawingText(OfficePackage package, XElement paragraph)
    {
        var text = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            string? value = node.Name == A + "t" ? node.Value : node.Name == A + "br" ? "\n" : node.Name == A + "tab" ? "\t" : null;
            if (value is not null) { package.AddText(value); text.Append(value); }
        }
        return text.ToString();
    }

    private static void ReadImages(OfficePackage package, XElement content, string part, List<OfficeContentBlock> blocks, CancellationToken ct)
    {
        foreach (var image in content.DescendantsAndSelf().Where(e => e.Name == A + "blip" || e.Name == V + "imagedata"))
        {
            ct.ThrowIfCancellationRequested();
            if (Deleted(image)) continue;
            var id = (string?)image.Attribute(R + "embed") ?? (string?)image.Attribute(R + "id");
            if (id is null)
            {
                if (image.Attribute(R + "link") is not null) package.Warn("External linked Office images are not fetched.");
                continue;
            }
            var target = Resolve(package, part, id, "image");
            if (target is not null) Add(package, blocks, new OfficeContentBlock { Kind = OfficeContentKind.Image, ImagePart = target });
        }
    }

    private static string? Resolve(OfficePackage package, string source, string? id, string expectedType)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("Office content has a missing relationship identifier.");
        var relationship = package.Relationships(source).SingleOrDefault(r => r.Id == id)
            ?? throw new InvalidDataException("Office content relationship does not exist.");
        if (relationship.Type != "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + expectedType &&
            relationship.Type != "http://purl.oclc.org/ooxml/officeDocument/relationships/" + expectedType)
            throw new InvalidDataException($"Office content requires a {expectedType} relationship.");
        var target = package.ResolveRelationship(source, id);
        if (target is null) package.Warn("External Office relationship is not followed.");
        return target;
    }
    private static void WarnObjects(OfficePackage package, XElement content)
    {
        XNamespace chart = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        XNamespace office = "urn:schemas-microsoft-com:office:office";
        if (content.Descendants().Any(e => !Deleted(e) && (e.Name == chart + "chart" || e.Name == office + "OLEObject" || e.Name == P + "oleObj" || e.Name == W + "object")))
            package.Warn("Office charts and embedded OLE objects are not rendered or executed; native surrounding text is retained.");
    }
    private static bool Deleted(XElement element) => element.AncestorsAndSelf().Any(e => e.Name == W + "del" || e.Name == W + "moveFrom"
        || (e.Name == W + "tr" && e.Element(W + "trPr")?.Element(W + "del") is not null)
        || (e.Name == W + "tc" && e.Element(W + "tcPr")?.Element(W + "cellDel") is not null));
    private static bool IsTrue(string? value) => value is "1" or "true";
    private static int Positive(string? value) => value is null ? 1 : int.TryParse(value, out int result) && result is > 0 and <= 16384 ? result
        : throw new InvalidDataException("Office table span must be a positive integer.");
    private static int Nonnegative(string? value) => value is null ? 0 : int.TryParse(value, out int result) && result is >= 0 and <= 16384 ? result
        : throw new InvalidDataException("Office grid offset must be a nonnegative integer.");
    private static void Add(OfficePackage package, List<OfficeContentBlock> blocks, OfficeContentBlock block)
    {
        package.AddBlock();
        blocks.Add(block);
    }

    private static void CheckDepth(int depth)
    {
        if (depth > 64) throw new InvalidDataException("Office content nesting exceeds the supported depth of 64.");
    }

    private static string JoinParagraphs(OfficePackage package, IEnumerable<string> paragraphs)
    {
        var text = new StringBuilder();
        bool first = true;
        foreach (var paragraph in paragraphs)
        {
            if (!first) { package.AddText("\n"); text.Append('\n'); }
            text.Append(paragraph);
            first = false;
        }
        return text.ToString();
    }

    private static string TableText(OfficePackage package, IReadOnlyList<OfficeTableCell> cells)
    {
        var text = new StringBuilder();
        int? previousRow = null;
        foreach (var cell in cells.OrderBy(c => c.Row).ThenBy(c => c.Column))
        {
            if (previousRow is not null)
            {
                string separator = previousRow == cell.Row ? "\t" : "\n";
                package.AddText(separator);
                text.Append(separator);
            }
            text.Append(cell.Text); // Native characters were charged when each cell was parsed.
            previousRow = cell.Row;
        }
        return text.ToString();
    }

    private static void SelectCompatibilityBranches(XElement root)
    {
        XNamespace mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        foreach (var alternate in root.Descendants(mc + "AlternateContent").Reverse().ToArray())
        {
            // Prefer the standard fallback to unsupported extension markup; never read both representations.
            var selected = alternate.Element(mc + "Fallback") ?? alternate.Elements(mc + "Choice").FirstOrDefault();
            if (selected is null) alternate.Remove();
            else alternate.ReplaceWith(selected.Nodes().ToArray());
        }
    }

    private static void NormalizeStrictNamespaces(XElement? root)
    {
        if (root is null) return;
        foreach (var element in root.DescendantsAndSelf())
        {
            element.Name = Normalize(element.Name);
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToArray())
            {
                var name = Normalize(attribute.Name);
                if (name == attribute.Name) continue;
                if (element.Attribute(name) is not null) throw new InvalidDataException("Conflicting Strict/transitional Office attributes.");
                string value = attribute.Value;
                attribute.Remove();
                element.Add(new XAttribute(name, value));
            }
        }
        static XName Normalize(XName name)
        {
            XNamespace? target = name.NamespaceName switch
            {
                "http://purl.oclc.org/ooxml/wordprocessingml/main" => W,
                "http://purl.oclc.org/ooxml/drawingml/main" => A,
                "http://purl.oclc.org/ooxml/presentationml/main" => P,
                "http://purl.oclc.org/ooxml/officeDocument/relationships" => R,
                _ => null,
            };
            return target is null ? name : target + name.LocalName;
        }
    }
}
