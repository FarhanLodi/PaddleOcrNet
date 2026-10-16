using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PaddleOcrNet.Structure;

/// <summary>Explicitly confirmed table continuation; automatic candidate inference never calls this API.</summary>
public static class DocumentTableJoining
{
    /// <summary>Create separate table views from caller-approved continuation candidates. Requires unambiguous
    /// chain topology, adjacent original pages, consistent columns and identical first rows. Repeated first rows
    /// are omitted only in the view and recorded in its omission log. Invalid spans or malformed table markup fail
    /// instead of being flattened. Empty approvals return no joined views.</summary>
    public static IReadOnlyList<DocumentTableView> CreateJoinedTables(this DocumentStructure document,
        IEnumerable<DocumentRelationship> approvedContinuations)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(approvedContinuations);
        var links = approvedContinuations.ToArray();
        if (links.Length == 0) return Array.Empty<DocumentTableView>();
        if (links.Any(l => l is null || l.Kind != DocumentRelationshipKind.TableContinuationCandidate || !document.Relationships.Contains(l)))
            throw new ArgumentException("Only continuation candidates belonging to this document can be approved.", nameof(approvedContinuations));
        if (links.Select(l => l.From).Distinct().Count() != links.Length || links.Select(l => l.To).Distinct().Count() != links.Length)
            throw new ArgumentException("Approved continuations must form unambiguous chains without duplicate or branching links.", nameof(approvedContinuations));

        var outgoing = links.ToDictionary(l => l.From);
        var destinations = links.Select(l => l.To).ToHashSet();
        var parsed = new Dictionary<DocumentBlockReference, ParsedTable>();
        foreach (var link in links)
        {
            if ((long)link.To.PageNumber != (long)link.From.PageNumber + 1)
                throw new ArgumentException("Table continuation requires adjacent original pages.", nameof(approvedContinuations));
            var first = GetTable(link.From);
            var next = GetTable(link.To);
            if (first.Columns != next.Columns || !SameHeader(first.Rows[0], next.Rows[0]))
                throw new ArgumentException("Confirmed tables must have matching column counts and identical first-row text, columns and spans.", nameof(approvedContinuations));
        }

        var views = new List<DocumentTableView>();
        foreach (var start in outgoing.Keys.Where(k => !destinations.Contains(k)).OrderBy(k => k.PageNumber).ThenBy(k => k.BlockIndex))
        {
            var sources = new List<DocumentBlockReference>();
            var rows = new List<DocumentTableRow>();
            var omitted = new List<DocumentTableRow>();
            var current = start;
            while (true)
            {
                var table = parsed[current];
                sources.Add(current);
                int firstRow = sources.Count == 1 ? 0 : 1;
                if (firstRow == 1) omitted.Add(table.Rows[0]);
                rows.AddRange(table.Rows.Skip(firstRow));
                if (!outgoing.TryGetValue(current, out var next)) break;
                current = next.To;
            }
            views.Add(new DocumentTableView { Sources = sources.ToArray(), ColumnCount = parsed[start].Columns,
                Rows = rows.ToArray(), OmittedHeaders = omitted.ToArray() });
        }
        return views.ToArray();

        ParsedTable GetTable(DocumentBlockReference reference)
        {
            if (parsed.TryGetValue(reference, out var cached)) return cached;
            var page = document.Pages.SingleOrDefault(p => p.PageNumber == reference.PageNumber)
                ?? throw new ArgumentException("A continuation references a missing page.", nameof(approvedContinuations));
            if (reference.BlockIndex < 0 || reference.BlockIndex >= page.Structure.Blocks.Count ||
                page.Structure.Blocks[reference.BlockIndex].Type != StructureBlockType.Table)
                throw new ArgumentException("A continuation references a missing or non-table block.", nameof(approvedContinuations));
            var table = Parse(page.Structure.Blocks[reference.BlockIndex].TableHtml, reference);
            parsed.Add(reference, table);
            return table;
        }
    }

    private sealed record ParsedTable(int Columns, IReadOnlyList<DocumentTableRow> Rows);

    private static bool SameHeader(DocumentTableRow first, DocumentTableRow next)
        => first.Cells.Count == next.Cells.Count && first.Cells.Count > 0 && first.Cells.Zip(next.Cells).All(pair =>
            pair.First.ColumnIndex == pair.Second.ColumnIndex && pair.First.ColumnSpan == pair.Second.ColumnSpan &&
            pair.First.RowSpan == 1 && pair.Second.RowSpan == 1 && Normalize(pair.First.Text).Length > 0 &&
            Normalize(pair.First.Text) == Normalize(pair.Second.Text));

    private static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static ParsedTable Parse(string? html, DocumentBlockReference source)
    {
        if (string.IsNullOrWhiteSpace(html)) throw new ArgumentException("A confirmed table must contain table markup.");
        if (html.Length > 4_000_000) throw new ArgumentException("Confirmed table markup exceeds the four-million-character limit.", nameof(html));
        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(html), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000,
            });
            root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
            // Validate the entire document, including content following its root.
            while (reader.Read()) { }
        }
        catch (XmlException ex)
        {
            throw new ArgumentException("Confirmed joining requires well-formed table XML/HTML; malformed or ambiguous markup is not repaired.", nameof(html), ex);
        }
        var tables = root.DescendantsAndSelf().Where(e => Name(e) == "table").ToArray();
        if (tables.Length != 1) throw new ArgumentException("Confirmed joining requires exactly one table without nested tables.", nameof(html));
        var table = tables[0];
        // Preserve document order across optional row groups.
        var elements = table.Descendants().Where(e => Name(e) == "tr").ToArray();
        if (elements.Length < 2) throw new ArgumentException("Each confirmed table needs a first row and at least one body row.", nameof(html));
        foreach (var row in elements)
        {
            bool direct = row.Parent == table;
            bool grouped = row.Parent is { } group && group.Parent == table && Name(group) is "thead" or "tbody" or "tfoot";
            if ((!direct && !grouped) || row.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                throw new ArgumentException("Table rows must be direct children of the table or its row groups and contain only cells.", nameof(html));
        }
        var rows = new List<DocumentTableRow>();
        var occupiedUntil = new Dictionary<int, int>();
        int columns = 0;
        for (int r = 0; r < elements.Length; r++)
        {
            var cells = new List<DocumentTableCell>();
            int column = 0;
            foreach (var cell in elements[r].Elements())
            {
                if (Name(cell) is not ("th" or "td")) throw new ArgumentException("A table row contains unsupported cell markup.", nameof(html));
                while (occupiedUntil.GetValueOrDefault(column) > r) column++;
                int colSpan = Span(cell, "colspan"), rowSpan = Span(cell, "rowspan");
                if (rowSpan > elements.Length - r || (r == 0 && rowSpan != 1) || column > 4096 - colSpan)
                    throw new ArgumentException("A cell span crosses a table/header boundary or exceeds the column limit.", nameof(html));
                if (elements[r].Parent != elements[r + rowSpan - 1].Parent)
                    throw new ArgumentException("A rowspan cannot cross an HTML row-group boundary.", nameof(html));
                for (int c = column; c < column + colSpan; c++)
                {
                    if (occupiedUntil.GetValueOrDefault(c) > r)
                        throw new ArgumentException("Overlapping table cell spans are ambiguous.", nameof(html));
                    occupiedUntil[c] = r + rowSpan;
                }
                cells.Add(new DocumentTableCell { ColumnIndex = column, ColumnSpan = colSpan, RowSpan = rowSpan, Text = CellText(cell) });
                column += colSpan;
            }
            int width = occupiedUntil.Where(kv => kv.Value > r).Select(kv => kv.Key + 1).DefaultIfEmpty().Max();
            if (r == 0) columns = width;
            if (columns < 2 || width != columns || Enumerable.Range(0, columns).Any(c => occupiedUntil.GetValueOrDefault(c) <= r))
                throw new ArgumentException("Table rows must cover the same logical columns without holes.", nameof(html));
            rows.Add(new DocumentTableRow { Source = source, SourceRowIndex = r, Cells = cells.ToArray() });
        }
        return new ParsedTable(columns, rows.ToArray());
    }

    private static string Name(XElement element) => element.Name.LocalName.ToLowerInvariant();
    private static int Span(XElement element, string name)
    {
        var attributes = element.Attributes().Where(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (attributes.Length == 0) return 1;
        if (attributes.Length != 1) throw new ArgumentException("Duplicate case-insensitive cell span attributes are ambiguous.");
        var attribute = attributes[0];
        if (!int.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1 || value > 4096)
            throw new ArgumentException("Cell spans must be positive integers no greater than 4096.");
        return value;
    }

    private static string CellText(XElement cell)
    {
        var text = new StringBuilder();
        foreach (var node in cell.DescendantNodes())
        {
            if (node is XText value) text.Append(value.Value);
            else if (node is XElement element && Name(element) == "br") text.Append('\n');
        }
        return text.ToString();
    }
}
