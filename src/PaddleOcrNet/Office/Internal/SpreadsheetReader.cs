using System.Globalization;
using System.Xml.Linq;

namespace PaddleOcrNet.Office.Internal;

/// <summary>Reads source spreadsheet content; never evaluates formulas or renders display formats.</summary>
internal static class SpreadsheetReader
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string StrictSpreadsheetNamespace = "http://purl.oclc.org/ooxml/spreadsheetml/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string StrictRelationshipNamespace = "http://purl.oclc.org/ooxml/officeDocument/relationships";
    internal static IReadOnlyList<OfficeDocumentSection> Read(OfficePackage package, string mainPart, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var workbook = package.ReadXml(mainPart);
        RequireSpreadsheetRoot(workbook, "workbook");
        var workbookRelationships = package.Relationships(mainPart);
        var sharedRelationship = workbookRelationships.FirstOrDefault(r => IsRelationship(r.Type, "sharedStrings"));
        string[] sharedStrings = [];
        if (sharedRelationship?.Target is { } stringsPart)
        {
            var strings = package.ReadXml(stringsPart);
            RequireSpreadsheetRoot(strings, "sst");
            sharedStrings = strings.Root!.Elements().Where(e => IsSpreadsheet(e, "si")).Select(ReadRichText).ToArray();
        }
        else if (sharedRelationship is not null) package.Warn("External shared-string relationship was not followed.");
        var sections = new List<OfficeDocumentSection>();
        foreach (var sheet in workbook.Root!.Elements().FirstOrDefault(e => IsSpreadsheet(e, "sheets"))?.Elements()
                     .Where(e => IsSpreadsheet(e, "sheet")) ?? Enumerable.Empty<XElement>())
        {
            ct.ThrowIfCancellationRequested();
            string id = RelationshipId(sheet, "id") ?? throw new InvalidDataException("A workbook sheet is missing its relationship ID.");
            string? part = package.ResolveRelationship(mainPart, id);
            if (part is null) { package.Warn("External worksheet relationship was not followed."); continue; }
            package.AddSection();
            var blocks = new List<OfficeContentBlock>();
            var relation = workbookRelationships.FirstOrDefault(r => r.Id == id);
            if (relation is null || !IsRelationship(relation.Type, "worksheet"))
                package.Warn($"Unsupported workbook sheet type: {relation?.Type ?? "unknown"}.");
            else
                ReadSheet(package, part, sharedStrings, blocks, ct);
            sections.Add(new OfficeDocumentSection
            {
                Index = sections.Count + 1, Name = (string?)sheet.Attribute("name") ?? $"Sheet {sections.Count + 1}",
                SourcePart = part, Blocks = blocks,
            });
        }
        return sections;
    }

    private static void ReadSheet(OfficePackage package, string part, IReadOnlyList<string> sharedStrings,
        List<OfficeContentBlock> blocks, CancellationToken ct)
    {
        var sheet = package.ReadXml(part);
        RequireSpreadsheetRoot(sheet, "worksheet");
        var merges = ReadMerges(sheet, package.Options.MaxBlocks, ct);
        var seen = new HashSet<(int Row, int Column)>();
        int inferredRow = 0;
        foreach (var row in sheet.Root!.Elements().FirstOrDefault(e => IsSpreadsheet(e, "sheetData"))?.Elements()
                     .Where(e => IsSpreadsheet(e, "row")) ?? Enumerable.Empty<XElement>())
        {
            ct.ThrowIfCancellationRequested();
            string? declaredRow = (string?)row.Attribute("r");
            int rowNumber = declaredRow is null ? checked(inferredRow + 1)
                : Positive(declaredRow) ?? throw new InvalidDataException("Invalid explicit spreadsheet row number.");
            inferredRow = rowNumber;
            int inferredColumn = 0;
            foreach (var cell in row.Elements().Where(e => IsSpreadsheet(e, "c")))
            {
                ct.ThrowIfCancellationRequested();
                string? sourceReference = (string?)cell.Attribute("r");
                int cellRow = rowNumber, column = checked(inferredColumn + 1);
                if (sourceReference is not null && !TryAddress(sourceReference, out cellRow, out column))
                    throw new InvalidDataException($"Invalid spreadsheet cell reference: {sourceReference}.");
                if (cellRow != rowNumber)
                    throw new InvalidDataException("Spreadsheet cell reference disagrees with its containing row.");
                if (!seen.Add((cellRow, column)))
                    throw new InvalidDataException("Duplicate spreadsheet cell reference.");
                inferredColumn = column;
                string reference = sourceReference ?? Address(cellRow, column);
                string? type = (string?)cell.Attribute("t");
                string? value = cell.Elements().FirstOrDefault(e => IsSpreadsheet(e, "v"))?.Value;
                string text;
                if (type == "s")
                {
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                        || index < 0 || index >= sharedStrings.Count)
                        throw new InvalidDataException($"Invalid shared-string index for cell {reference}.");
                    text = sharedStrings[index];
                }
                else if (type == "inlineStr")
                    text = cell.Elements().FirstOrDefault(e => IsSpreadsheet(e, "is")) is { } inline ? ReadRichText(inline) : string.Empty;
                else text = value ?? string.Empty; // Keep numeric, Boolean, error and cached formula values verbatim.
                var formula = cell.Elements().FirstOrDefault(e => IsSpreadsheet(e, "f"));
                if (formula is not null && formula.Value.Length == 0 && (string?)formula.Attribute("t") == "shared")
                    package.Warn($"Shared formula in {part}#{reference} has no inline expression; cached value retained without expansion.");
                var span = merges.GetValueOrDefault((cellRow, column), (1, 1));
                package.AddText(text);
                if (formula is not null) package.AddText(formula.Value);
                package.AddBlock();
                blocks.Add(new OfficeContentBlock
                {
                    Kind = OfficeContentKind.Cell, Text = text, CellReference = reference, Formula = formula?.Value,
                    Cells = [new OfficeTableCell { Row = cellRow, Column = column, RowSpan = span.Item1, ColumnSpan = span.Item2, Text = text }],
                });
            }
        }
        // A merge can exist without a serialized top-left cell. Retain that structural anchor too.
        foreach (var (address, span) in merges)
        {
            ct.ThrowIfCancellationRequested();
            if (seen.Contains(address)) continue;
            package.AddBlock();
            blocks.Add(new OfficeContentBlock
            {
                Kind = OfficeContentKind.Cell, CellReference = Address(address.Row, address.Column),
                Cells = [new OfficeTableCell { Row = address.Row, Column = address.Column, RowSpan = span.RowSpan, ColumnSpan = span.ColumnSpan }],
            });
        }
        foreach (var drawing in sheet.Root!.Elements().Where(e => IsSpreadsheet(e, "drawing")))
        {
            ct.ThrowIfCancellationRequested();
            string id = RelationshipId(drawing, "id") ?? throw new InvalidDataException("Worksheet drawing lacks a relationship ID.");
            if (!package.Relationships(part).Any(r => r.Id == id && IsRelationship(r.Type, "drawing")))
                throw new InvalidDataException("Worksheet drawing must reference a drawing relationship.");
            string? drawingPart = package.ResolveRelationship(part, id);
            if (drawingPart is null) { package.Warn("External worksheet drawing was not followed."); continue; }
            ReadDrawing(package, drawingPart, blocks, ct);
        }
        foreach (var feature in sheet.Root!.Elements().Where(e => IsSpreadsheet(e, "legacyDrawing") || IsSpreadsheet(e, "oleObjects") || IsSpreadsheet(e, "controls")))
            package.Warn($"Unsupported spreadsheet feature {feature.Name.LocalName} in {part}.");
    }

    private static void ReadDrawing(OfficePackage package, string part, List<OfficeContentBlock> blocks, CancellationToken ct)
    {
        var drawing = package.ReadXml(part);
        if (drawing.Root is not { } root || !IsDrawing(root, "wsDr"))
            throw new InvalidDataException("Expected an OOXML spreadsheet drawing root.");
        foreach (var anchor in root.Elements().Where(e => IsDrawing(e, "oneCellAnchor") || IsDrawing(e, "twoCellAnchor") || IsDrawing(e, "absoluteAnchor")))
        {
            ct.ThrowIfCancellationRequested();
            string? cellReference = null;
            var from = anchor.Elements().FirstOrDefault(e => IsDrawing(e, "from"));
            if (from is not null)
            {
                var row = Nonnegative(from.Elements().FirstOrDefault(e => IsDrawing(e, "row"))?.Value);
                var col = Nonnegative(from.Elements().FirstOrDefault(e => IsDrawing(e, "col"))?.Value);
                if (row is not null && col is not null && row < int.MaxValue && col < int.MaxValue)
                    cellReference = Address(row.Value + 1, col.Value + 1);
                else package.Warn($"Drawing in {part} has an invalid cell anchor.");
            }
            foreach (var blip in anchor.Descendants().Where(e => e.Name.LocalName == "blip"
                         && e.Name.NamespaceName is "http://schemas.openxmlformats.org/drawingml/2006/main" or "http://purl.oclc.org/ooxml/drawingml/main"))
            {
                ct.ThrowIfCancellationRequested();
                string? id = RelationshipId(blip, "embed");
                if (id is null) { package.Warn("Linked or unidentified spreadsheet image was not followed."); continue; }
                if (!package.Relationships(part).Any(r => r.Id == id && IsRelationship(r.Type, "image")))
                    throw new InvalidDataException("Spreadsheet image must reference an image relationship.");
                string? imagePart = package.ResolveRelationship(part, id);
                if (imagePart is null) { package.Warn("External spreadsheet image was not followed."); continue; }
                package.AddBlock();
                blocks.Add(new OfficeContentBlock { Kind = OfficeContentKind.Image, ImagePart = imagePart, CellReference = cellReference });
            }
            if (anchor.Descendants().Any(e => e.Name.LocalName == "chart"
                    && e.Name.NamespaceName is "http://schemas.openxmlformats.org/drawingml/2006/chart" or "http://purl.oclc.org/ooxml/drawingml/chart"))
                package.Warn($"Chart content in {part} is unsupported; no chart values or rendering were inferred.");
        }
    }

    private static Dictionary<(int Row, int Column), (int RowSpan, int ColumnSpan)> ReadMerges(XDocument sheet, int maxBlocks, CancellationToken ct)
    {
        var result = new Dictionary<(int, int), (int, int)>();
        foreach (var merge in sheet.Root!.Elements().FirstOrDefault(e => IsSpreadsheet(e, "mergeCells"))?.Elements()
                     .Where(e => IsSpreadsheet(e, "mergeCell")) ?? Enumerable.Empty<XElement>())
        {
            ct.ThrowIfCancellationRequested();
            string[] range = ((string?)merge.Attribute("ref") ?? string.Empty).Split(':');
            if (range.Length != 2 || !TryAddress(range[0], out int row, out int col)
                || !TryAddress(range[1], out int endRow, out int endCol) || endRow < row || endCol < col)
                throw new InvalidDataException("Invalid spreadsheet merged-cell range.");
            if (!result.TryAdd((row, col), (endRow - row + 1, endCol - col + 1)))
                throw new InvalidDataException("Duplicate spreadsheet merged-cell anchor.");
            if (result.Count > maxBlocks) throw new InvalidDataException("Spreadsheet merged-cell block limit exceeded.");
        }
        return result;
    }

    private static string ReadRichText(XElement element) => string.Concat(element.Elements().SelectMany(e =>
        IsSpreadsheet(e, "t") ? new[] { e.Value } : IsSpreadsheet(e, "r")
            ? e.Elements().Where(t => IsSpreadsheet(t, "t")).Select(t => t.Value) : Enumerable.Empty<string>()));

    private static string? RelationshipId(XElement element, string name) => element.Attributes()
        .FirstOrDefault(a => a.Name.LocalName == name && a.Name.NamespaceName is RelationshipNamespace or StrictRelationshipNamespace)?.Value;
    private static bool IsRelationship(string type, string name) => type == RelationshipNamespace + "/" + name || type == StrictRelationshipNamespace + "/" + name;
    private static bool IsSpreadsheet(XElement element, string name) => element.Name.LocalName == name
        && element.Name.NamespaceName is SpreadsheetNamespace or StrictSpreadsheetNamespace;
    private static bool IsDrawing(XElement element, string name) => element.Name.LocalName == name
        && element.Name.NamespaceName is "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" or "http://purl.oclc.org/ooxml/drawingml/spreadsheetDrawing";
    private static void RequireSpreadsheetRoot(XDocument document, string name)
    {
        if (document.Root is not { } root || !IsSpreadsheet(root, name))
            throw new InvalidDataException($"Expected OOXML spreadsheet {name} root.");
    }
    private static int? Positive(string? text) => Nonnegative(text) is int value && value > 0 ? value : null;
    private static int? Nonnegative(string? text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 0 ? value : null;

    private static bool TryAddress(string reference, out int row, out int column)
    {
        row = column = 0;
        int i = 0;
        while (i < reference.Length && reference[i] is >= 'A' and <= 'Z')
        {
            if (column > (int.MaxValue - 26) / 26) return false;
            column = column * 26 + reference[i++] - 'A' + 1;
        }
        return column > 0 && i < reference.Length && int.TryParse(reference.AsSpan(i), NumberStyles.None,
            CultureInfo.InvariantCulture, out row) && row > 0;
    }

    private static string Address(int row, int column)
    {
        string letters = string.Empty;
        while (column > 0) { column--; letters = (char)('A' + column % 26) + letters; column /= 26; }
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }
}
