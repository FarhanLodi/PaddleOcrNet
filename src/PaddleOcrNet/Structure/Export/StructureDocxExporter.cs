using System.IO.Compression;
using System.Text;

namespace PaddleOcrNet.Structure.Export;

/// <summary>
/// Exports a <see cref="StructureResult"/> to a Microsoft Word <c>.docx</c> (WordprocessingML) package,
/// reaching parity with Python PaddleOCR's PP-StructureV3 <c>save_to_word()</c>. The exporter walks the
/// analyzed <see cref="StructureResult.Blocks"/> in reading order and maps each block kind onto a Word
/// construct:
/// <list type="bullet">
///   <item>doc/section titles → heading-styled paragraphs (<c>Heading1</c>/<c>Heading2</c>);</item>
///   <item>body text / lists / captions → normal paragraphs;</item>
///   <item>tables → native Word <c>&lt;w:tbl&gt;</c> recovered from the block's HTML grid, honoring
///         <c>colspan</c> (via <c>&lt;w:gridSpan&gt;</c>) and <c>rowspan</c> (via <c>&lt;w:vMerge&gt;</c>);</item>
///   <item>formulas → a paragraph carrying the recovered LaTeX as <c>$$…$$</c> text;</item>
///   <item>figures/charts/seals → a placeholder paragraph noting the figure region (the pixels are not
///         embedded in v1).</item>
/// </list>
/// The package is built by hand (a <c>.docx</c> is a ZIP of XML parts) using only the BCL, so the library
/// stays dependency-free and Native-AOT safe. All emitted text is XML-escaped.
/// </summary>
public static class StructureDocxExporter
{
    // The minimal WordprocessingML namespaces. Only the main "w" namespace and the relationship namespace
    // are required for the parts this exporter emits.
    private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    // UTF-8 without a BOM: OOXML parts must not carry a byte-order mark.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Renders the structure result to an in-memory WordprocessingML <c>.docx</c> package.
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <returns>The bytes of a valid <c>.docx</c> ZIP package.</returns>
    public static byte[] ToDocx(this StructureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        using var buffer = new MemoryStream();
        WriteDocx(result, buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Writes the structure result as a WordprocessingML <c>.docx</c> package into <paramref name="destination"/>.
    /// The stream is left open so callers retain ownership.
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <param name="destination">The writable, seekable stream to receive the package. Must not be <c>null</c>.</param>
    public static void WriteDocx(this StructureResult result, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(destination);
        WriteDocx(result, destination, leaveOpen: true);
    }

    /// <summary>
    /// Renders the structure result and saves it to <paramref name="path"/> as a <c>.docx</c> file.
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <param name="path">The destination file path. Must not be <c>null</c> or empty.</param>
    public static void SaveAsDocx(this StructureResult result, string path)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var file = File.Create(path);
        WriteDocx(result, file, leaveOpen: false);
    }

    private static void WriteDocx(StructureResult result, Stream destination, bool leaveOpen)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen);

        WriteEntry(zip, "[Content_Types].xml", ContentTypesXml());
        WriteEntry(zip, "_rels/.rels", RootRelsXml());
        WriteEntry(zip, "word/_rels/document.xml.rels", DocumentRelsXml());
        WriteEntry(zip, "word/document.xml", DocumentXml(result));
    }

    // ---- top-level OOXML parts -------------------------------------------------------------------------

    private static string ContentTypesXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/word/document.xml\" " +
        "ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "</Types>";

    private static string RootRelsXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" " +
        "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" " +
        "Target=\"word/document.xml\"/>" +
        "</Relationships>";

    // No relationships are required by the document body itself (no styles/numbering parts are emitted),
    // but Word expects the document part's .rels to exist. An empty relationship set is valid.
    private static string DocumentRelsXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>";

    // ---- document body ---------------------------------------------------------------------------------

    private static string DocumentXml(StructureResult result)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<w:document xmlns:w=\"").Append(WNs).Append("\">");
        sb.Append("<w:body>");

        foreach (var block in OrderedBlocks(result))
        {
            AppendBlock(sb, block);
        }

        // A trailing section-properties element is conventional and keeps Word happy about page setup.
        sb.Append("<w:sectPr/>");
        sb.Append("</w:body>");
        sb.Append("</w:document>");
        return sb.ToString();
    }

    private static void AppendBlock(StringBuilder sb, StructureBlock block)
    {
        switch (block.Type)
        {
            case StructureBlockType.DocTitle:
                AppendParagraph(sb, block.Text, styleId: "Title");
                break;

            case StructureBlockType.Title:
                AppendParagraph(sb, block.Text, styleId: "Heading1");
                break;

            case StructureBlockType.Abstract:
                AppendParagraph(sb, block.Text, styleId: "Heading2");
                break;

            case StructureBlockType.Table:
                AppendTable(sb, block);
                break;

            case StructureBlockType.Formula:
                // Word has no native LaTeX; emit the recovered LaTeX as $$…$$ text for a faithful, honest v1.
                // TODO(omml): convert the LaTeX to Office MathML (OMML) so Word renders the equation natively.
                if (!string.IsNullOrWhiteSpace(block.Latex))
                {
                    AppendParagraph(sb, "$$ " + block.Latex!.Trim() + " $$", styleId: null);
                }
                break;

            case StructureBlockType.Figure:
            case StructureBlockType.Chart:
            case StructureBlockType.Seal:
                // TODO(docx-image-embed): embed the figure pixels as a media part + drawing run. v1 records
                // only a textual placeholder so the figure region is never silently dropped.
                AppendFigurePlaceholder(sb, block);
                break;

            default:
                // Text, Paragraph, List, captions, headers/footers, references, footnotes, etc.
                AppendParagraph(sb, block.Text, styleId: null);
                break;
        }
    }

    /// <summary>Emits a single <c>&lt;w:p&gt;</c> with one run of <paramref name="text"/>; skips blank text.</summary>
    private static void AppendParagraph(StringBuilder sb, string? text, string? styleId)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        sb.Append("<w:p>");
        if (styleId is not null)
        {
            sb.Append("<w:pPr><w:pStyle w:val=\"").Append(styleId).Append("\"/></w:pPr>");
        }
        AppendRun(sb, text.Trim());
        sb.Append("</w:p>");
    }

    private static void AppendFigurePlaceholder(StringBuilder sb, StructureBlock block)
    {
        var caption = block.Text?.Trim();
        var label = "[" + block.Type + " region]";
        var text = string.IsNullOrEmpty(caption) ? label : label + " " + caption;
        AppendParagraph(sb, text, styleId: null);
    }

    /// <summary>
    /// Emits a run that preserves significant whitespace (<c>xml:space="preserve"</c>) so leading/trailing
    /// spaces inside cells/paragraphs survive the round-trip.
    /// </summary>
    private static void AppendRun(StringBuilder sb, string text)
    {
        sb.Append("<w:r><w:t xml:space=\"preserve\">").Append(Xml(text)).Append("</w:t></w:r>");
    }

    // ---- tables ----------------------------------------------------------------------------------------

    private static void AppendTable(StringBuilder sb, StructureBlock block)
    {
        var grid = OoxmlHtmlTable.Parse(block.TableHtml);

        // No parseable grid: degrade to a single-cell table carrying whatever text we have, so the table
        // region is never lost (this is the most faithful option when only free text is available).
        if (grid is null || grid.RowCount == 0 || grid.ColumnCount == 0)
        {
            var fallback = !string.IsNullOrWhiteSpace(block.Text)
                ? block.Text!.Trim()
                : (block.TableHtml?.Trim() ?? string.Empty);
            if (fallback.Length == 0) return;

            sb.Append("<w:tbl>");
            AppendTableProperties(sb);
            sb.Append("<w:tblGrid><w:gridCol/></w:tblGrid>");
            sb.Append("<w:tr>");
            AppendTableCell(sb, fallback, gridSpan: 1, vMerge: null);
            sb.Append("</w:tr>");
            sb.Append("</w:tbl>");
            // A trailing empty paragraph after a table is required so Word does not merge it with following
            // content; emit one to be safe.
            sb.Append("<w:p/>");
            return;
        }

        sb.Append("<w:tbl>");
        AppendTableProperties(sb);

        // Declare the logical column grid.
        sb.Append("<w:tblGrid>");
        for (int c = 0; c < grid.ColumnCount; c++)
        {
            sb.Append("<w:gridCol/>");
        }
        sb.Append("</w:tblGrid>");

        for (int r = 0; r < grid.RowCount; r++)
        {
            sb.Append("<w:tr>");
            for (int c = 0; c < grid.ColumnCount; c++)
            {
                var cell = grid.Cells[r, c];
                if (cell is null)
                {
                    // A slot covered by a colspan of the cell to its left: nothing to emit (it was folded
                    // into that cell's gridSpan).
                    continue;
                }

                if (cell.IsRowSpanContinuation)
                {
                    // A slot covered by a rowspan from above: emit a vertically-merged continuation cell.
                    AppendTableCell(sb, text: string.Empty, gridSpan: cell.ColSpan, vMerge: "continue");
                    continue;
                }

                var vMerge = cell.RowSpan > 1 ? "restart" : null;
                AppendTableCell(sb, cell.Text, gridSpan: cell.ColSpan, vMerge: vMerge);
            }
            sb.Append("</w:tr>");
        }

        sb.Append("</w:tbl>");
        sb.Append("<w:p/>");
    }

    private static void AppendTableProperties(StringBuilder sb)
    {
        // A single-line border on every edge so the table is visibly gridded, matching the recovered HTML.
        sb.Append("<w:tblPr>");
        sb.Append("<w:tblW w:w=\"0\" w:type=\"auto\"/>");
        sb.Append("<w:tblBorders>");
        foreach (var edge in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
        {
            sb.Append("<w:").Append(edge).Append(" w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/>");
        }
        sb.Append("</w:tblBorders>");
        sb.Append("</w:tblPr>");
    }

    private static void AppendTableCell(StringBuilder sb, string text, int gridSpan, string? vMerge)
    {
        sb.Append("<w:tc>");
        sb.Append("<w:tcPr>");
        if (gridSpan > 1)
        {
            sb.Append("<w:gridSpan w:val=\"").Append(gridSpan).Append("\"/>");
        }
        if (vMerge is not null)
        {
            // "restart" begins a vertical merge; "continue" extends it downward.
            sb.Append("<w:vMerge w:val=\"").Append(vMerge).Append("\"/>");
        }
        sb.Append("</w:tcPr>");

        // A table cell must contain at least one paragraph to be valid, even when empty.
        if (string.IsNullOrEmpty(text))
        {
            sb.Append("<w:p/>");
        }
        else
        {
            sb.Append("<w:p>");
            AppendRun(sb, text);
            sb.Append("</w:p>");
        }
        sb.Append("</w:tc>");
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static IEnumerable<StructureBlock> OrderedBlocks(StructureResult result)
        => result.Blocks.OrderBy(b => b.Order);

    /// <summary>Escapes XML's five predefined entities so arbitrary OCR text is safe in element content.</summary>
    internal static string Xml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    /// <summary>Adds a ZIP entry with the given (forward-slash, no leading slash) name and UTF-8 (no BOM) text.</summary>
    internal static void WriteEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(content);
    }
}

// =======================================================================================================
//  Shared lightweight HTML-<table> parser
// =======================================================================================================

/// <summary>
/// A minimal forgiving parser for the recovered table HTML carried on a
/// <see cref="StructureBlock.TableHtml"/>. PaddleOCR's table recognizer emits well-formed (if attribute-light)
/// <c>&lt;table&gt;…&lt;/table&gt;</c> markup whose cells may carry <c>rowspan</c>/<c>colspan</c>; this parser
/// materializes that into a rectangular <see cref="OoxmlTableGrid"/> with merge information so both the Word
/// and Excel exporters can lay the cells out faithfully. It is intentionally tolerant: it scans for
/// <c>&lt;tr&gt;</c> / <c>&lt;td&gt;</c> / <c>&lt;th&gt;</c> tags, ignores anything it does not understand, and
/// strips inner tags from cell content.
/// </summary>
internal static class OoxmlHtmlTable
{
    public static OoxmlTableGrid? Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var rows = new List<List<OoxmlRawCell>>();
        int i = 0;
        int n = html.Length;

        while (i < n)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0) break;
            int gt = html.IndexOf('>', lt + 1);
            if (gt < 0) break;

            string tag = html.Substring(lt + 1, gt - lt - 1).Trim();
            string lower = tag.ToLowerInvariant();

            if (lower == "tr" || lower.StartsWith("tr ") || lower.StartsWith("tr\t") || lower.StartsWith("tr\n"))
            {
                rows.Add(new List<OoxmlRawCell>());
                i = gt + 1;
                continue;
            }

            if (IsCellOpen(lower))
            {
                int colSpan = ReadSpan(tag, "colspan");
                int rowSpan = ReadSpan(tag, "rowspan");

                // The cell content runs from just after this open tag to its matching close tag.
                int contentStart = gt + 1;
                int close = FindCellClose(html, contentStart);
                int contentEnd = close < 0 ? n : close;
                string raw = html.Substring(contentStart, contentEnd - contentStart);
                string text = CleanCellText(raw);

                if (rows.Count == 0) rows.Add(new List<OoxmlRawCell>());
                rows[^1].Add(new OoxmlRawCell(text, Math.Max(1, colSpan), Math.Max(1, rowSpan)));

                // Resume scanning after the cell content (the close tag, if any, is re-scanned harmlessly).
                i = close < 0 ? n : close;
                continue;
            }

            i = gt + 1;
        }

        // Drop trailing empty rows produced by stray <tr> with no cells.
        rows.RemoveAll(r => r.Count == 0);
        if (rows.Count == 0) return null;

        return OoxmlTableGrid.FromRows(rows);
    }

    private static bool IsCellOpen(string lowerTag)
        => lowerTag == "td" || lowerTag == "th"
           || lowerTag.StartsWith("td ") || lowerTag.StartsWith("th ")
           || lowerTag.StartsWith("td\t") || lowerTag.StartsWith("th\t")
           || lowerTag.StartsWith("td\n") || lowerTag.StartsWith("th\n");

    /// <summary>Finds the index of the next <c>&lt;/td&gt;</c> or <c>&lt;/th&gt;</c> from <paramref name="from"/>.</summary>
    private static int FindCellClose(string html, int from)
    {
        int i = from;
        while (i < html.Length)
        {
            int lt = html.IndexOf("</", i, StringComparison.Ordinal);
            if (lt < 0) return -1;
            int gt = html.IndexOf('>', lt + 2);
            if (gt < 0) return -1;
            string name = html.Substring(lt + 2, gt - lt - 2).Trim().ToLowerInvariant();
            if (name == "td" || name == "th") return lt;
            i = gt + 1;
        }
        return -1;
    }

    private static int ReadSpan(string tag, string attr)
    {
        int idx = tag.IndexOf(attr, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 1;
        int eq = tag.IndexOf('=', idx);
        if (eq < 0) return 1;

        int j = eq + 1;
        while (j < tag.Length && (tag[j] == ' ' || tag[j] == '"' || tag[j] == '\'')) j++;
        int start = j;
        while (j < tag.Length && char.IsDigit(tag[j])) j++;
        if (j == start) return 1;
        return int.TryParse(tag.AsSpan(start, j - start), out int v) && v > 0 ? v : 1;
    }

    /// <summary>Strips any nested tags and decodes the handful of HTML entities PaddleOCR emits.</summary>
    private static string CleanCellText(string raw)
    {
        if (raw.Length == 0) return string.Empty;

        var sb = new StringBuilder(raw.Length);
        bool inTag = false;
        foreach (char ch in raw)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }

        return DecodeEntities(sb.ToString()).Trim();
    }

    private static string DecodeEntities(string s)
    {
        if (s.IndexOf('&') < 0) return s;
        return s
            .Replace("&nbsp;", " ")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&apos;", "'")
            .Replace("&amp;", "&");
    }
}

/// <summary>A raw cell as scanned from the HTML, before placement into the rectangular grid.</summary>
internal sealed record OoxmlRawCell(string Text, int ColSpan, int RowSpan);

/// <summary>A placed grid cell, carrying its text, its spans and whether it is a rowspan continuation slot.</summary>
internal sealed class OoxmlGridCell
{
    public string Text { get; init; } = string.Empty;
    public int ColSpan { get; init; } = 1;
    public int RowSpan { get; init; } = 1;

    /// <summary>True when this slot is occupied by a <c>rowspan</c> from a cell in an earlier row.</summary>
    public bool IsRowSpanContinuation { get; init; }
}

/// <summary>
/// A rectangular table grid recovered from HTML. <see cref="Cells"/> is <c>[row, col]</c>; a <c>null</c> slot
/// is one folded into a preceding cell's <c>colspan</c> (no element should be emitted for it), whereas a
/// <see cref="OoxmlGridCell.IsRowSpanContinuation"/> slot is a vertical-merge continuation that an exporter
/// renders as a merged/empty cell.
/// </summary>
internal sealed class OoxmlTableGrid
{
    public required OoxmlGridCell?[,] Cells { get; init; }
    public required int RowCount { get; init; }
    public required int ColumnCount { get; init; }

    public static OoxmlTableGrid FromRows(List<List<OoxmlRawCell>> rows)
    {
        int rowCount = rows.Count;

        // Column count is the widest row once colspans are accounted for. Because rowspans push cells down
        // into later rows, we resolve placement with an occupancy map and grow the column count as needed.
        // First pass: an upper bound on columns.
        int maxCols = 0;
        foreach (var row in rows)
        {
            int width = 0;
            foreach (var cell in row) width += cell.ColSpan;
            if (width > maxCols) maxCols = width;
        }
        if (maxCols == 0) maxCols = 1;

        // occupied[r,c] marks slots already taken by a rowspan/colspan placed earlier.
        var occupied = new bool[rowCount, maxCols];
        var cells = new OoxmlGridCell?[rowCount, maxCols];

        for (int r = 0; r < rowCount; r++)
        {
            int c = 0;
            foreach (var raw in rows[r])
            {
                // Skip to the next free slot in this row (one occupied by a rowspan from above).
                while (c < maxCols && occupied[r, c]) c++;
                if (c >= maxCols) break; // row overflows the grid; ignore extra cells defensively

                int colSpan = Math.Min(raw.ColSpan, maxCols - c);
                int rowSpan = Math.Min(raw.RowSpan, rowCount - r);

                cells[r, c] = new OoxmlGridCell
                {
                    Text = raw.Text,
                    ColSpan = colSpan,
                    RowSpan = rowSpan,
                    IsRowSpanContinuation = false,
                };

                // Mark the full span as occupied; for rows below the origin, plant continuation cells so the
                // exporters can emit vertical-merge placeholders that span the same columns.
                for (int dr = 0; dr < rowSpan; dr++)
                {
                    for (int dc = 0; dc < colSpan; dc++)
                    {
                        int rr = r + dr;
                        int cc = c + dc;
                        occupied[rr, cc] = true;
                        if (dr > 0 && dc == 0)
                        {
                            cells[rr, cc] = new OoxmlGridCell
                            {
                                Text = string.Empty,
                                ColSpan = colSpan,
                                RowSpan = 1,
                                IsRowSpanContinuation = true,
                            };
                        }
                    }
                }

                c += colSpan;
            }
        }

        return new OoxmlTableGrid { Cells = cells, RowCount = rowCount, ColumnCount = maxCols };
    }
}
