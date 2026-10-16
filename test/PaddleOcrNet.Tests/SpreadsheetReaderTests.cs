using System.IO.Compression;
using System.Text;
using PaddleOcrNet.Office;
using PaddleOcrNet.Office.Internal;
using Xunit;

namespace PaddleOcrNet.Tests;

public sealed class SpreadsheetReaderTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    [Fact]
    public void OutputBlockAndExpandedSharedTextBudgetsAreEnforced()
    {
        using var cells = SingleSheet(Sheet("""<row><c r="A1"><v>1</v></c><c r="B1"><v>2</v></c></row>"""));
        using var limitedBlocks = new OfficePackage(cells, new OfficeDocumentOptions { MaxBlocks = 1 }, default);
        Assert.Throws<InvalidDataException>(() => SpreadsheetReader.Read(limitedBlocks, "xl/workbook.xml", default));

        using var shared = Build(new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = $"""<workbook xmlns="{Main}" xmlns:r="{Rel}"><sheets><sheet name="Data" r:id="s"/></sheets></workbook>""",
            ["xl/_rels/workbook.xml.rels"] = Relationships(("s", "worksheet", "worksheets/sheet.xml"), ("text", "sharedStrings", "strings.xml")),
            ["xl/strings.xml"] = $"""<sst xmlns="{Main}"><si><t>123456</t></si></sst>""",
            ["xl/worksheets/sheet.xml"] = Sheet("""<row><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>0</v></c></row>"""),
        });
        using var limitedText = new OfficePackage(shared, new OfficeDocumentOptions { MaxTextCharacters = 10 }, default);
        Assert.Throws<InvalidDataException>(() => SpreadsheetReader.Read(limitedText, "xl/workbook.xml", default));
    }

    [Theory]
    [InlineData("<worksheet xmlns=\"urn:untrusted\"><sheetData><row><c><v>fake</v></c></row></sheetData></worksheet>")]
    [InlineData("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>")]
    public void WrongWorksheetRootOrNamespaceIsRejected(string xml)
    {
        using var stream = SingleSheet(xml);
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), default);
        Assert.Throws<InvalidDataException>(() => SpreadsheetReader.Read(package, "xl/workbook.xml", default));
    }

    [Fact]
    public void WorkbookOrderSharedRichTextAndRawCachedValuesArePreserved()
    {
        using var stream = Build(new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = $"""<workbook xmlns="{Main}" xmlns:r="{Rel}"><sheets><sheet name="Second" sheetId="2" r:id="r2"/><sheet name="First" sheetId="1" r:id="r1"/></sheets></workbook>""",
            ["xl/_rels/workbook.xml.rels"] = Relationships(("r1", "worksheet", "worksheets/a.xml"), ("r2", "worksheet", "worksheets/b.xml"), ("s", "sharedStrings", "sharedStrings.xml")),
            ["xl/sharedStrings.xml"] = $"""<sst xmlns="{Main}"><si><r><t xml:space="preserve"> café </t></r><r><t>😀</t></r><rPh sb="0" eb="1"><t>phonetic</t></rPh></si></sst>""",
            ["xl/worksheets/b.xml"] = Sheet("""<row r="4"><c r="C4" t="s"><v>0</v></c><c r="D4"><v>001.2300</v></c><c r="E4" t="b"><v>1</v></c><c r="F4" t="e"><v>#DIV/0!</v></c><c r="G4"><f>SUM(D4:E4)</f><v>2.23</v></c></row>"""),
            ["xl/worksheets/a.xml"] = Sheet("""<row r="1"><c r="A1" t="inlineStr"><is><r><t>Hello </t></r><r><t>world</t></r></is></c></row>"""),
        });
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), default);
        var sections = SpreadsheetReader.Read(package, "xl/workbook.xml", default);
        Assert.Equal(new[] { "Second", "First" }, sections.Select(s => s.Name));
        Assert.Equal(new[] { 1, 2 }, sections.Select(s => s.Index));
        Assert.Equal(new[] { " café 😀", "001.2300", "1", "#DIV/0!", "2.23" }, sections[0].Blocks.Select(b => b.Text));
        Assert.Equal("SUM(D4:E4)", sections[0].Blocks[^1].Formula);
        Assert.Equal("G4", sections[0].Blocks[^1].CellReference);
        Assert.Equal("Hello world", Assert.Single(sections[1].Blocks).Text);
    }

    [Fact]
    public void MergesRetainSpansAndEmptyAnchorsWithoutDroppingCoveredSourceCells()
    {
        using var stream = SingleSheet(Sheet("""<row r="2"><c r="B2" t="inlineStr"><is><t>header</t></is></c><c r="C2" t="inlineStr"><is><t>source evidence</t></is></c></row>""",
            """<mergeCells><mergeCell ref="B2:D3"/><mergeCell ref="F7:G8"/></mergeCells>"""));
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), default);
        var blocks = Assert.Single(SpreadsheetReader.Read(package, "xl/workbook.xml", default)).Blocks;
        var merged = Assert.Single(blocks.First(b => b.CellReference == "B2").Cells);
        Assert.Equal((2, 2, 2, 3), (merged.Row, merged.Column, merged.RowSpan, merged.ColumnSpan));
        Assert.Equal("source evidence", blocks.First(b => b.CellReference == "C2").Text);
        var empty = blocks.First(b => b.CellReference == "F7");
        Assert.Equal(string.Empty, empty.Text);
        Assert.Equal((2, 2), (empty.Cells[0].RowSpan, empty.Cells[0].ColumnSpan));
    }

    [Fact]
    public void DrawingImagesResolveThroughDrawingRelationshipsAndRetainAnchor()
    {
        using var stream = SingleSheet(Sheet("", """<drawing r:id="draw"/>"""), new Dictionary<string, string>
        {
            ["xl/worksheets/_rels/sheet1.xml.rels"] = Relationships(("draw", "drawing", "../drawings/drawing1.xml")),
            ["xl/drawings/drawing1.xml"] = $"""<xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="{Rel}"><xdr:twoCellAnchor><xdr:from><xdr:col>2</xdr:col><xdr:row>4</xdr:row></xdr:from><xdr:pic><a:blip r:embed="image"/></xdr:pic></xdr:twoCellAnchor></xdr:wsDr>""",
            ["xl/drawings/_rels/drawing1.xml.rels"] = Relationships(("image", "image", "../media/image.png")),
            ["xl/media/image.png"] = "image bytes not decoded by native content reader",
        });
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), default);
        var block = Assert.Single(Assert.Single(SpreadsheetReader.Read(package, "xl/workbook.xml", default)).Blocks);
        Assert.Equal(OfficeContentKind.Image, block.Kind);
        Assert.Equal("xl/media/image.png", block.ImagePart);
        Assert.Equal("C5", block.CellReference);
    }

    [Fact]
    public void FormulaWithoutCacheRemainsUnevaluatedAndMissingCellReferencesFollowXmlOrder()
    {
        using var stream = SingleSheet(Sheet("""<row r="3"><c><f>HYPERLINK("https://example.invalid","do not execute")</f></c><c><f t="shared" si="0"/><v>9</v></c><c t="inlineStr"><is><t>é</t></is></c></row>"""));
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), default);
        var blocks = Assert.Single(SpreadsheetReader.Read(package, "xl/workbook.xml", default)).Blocks;
        Assert.Equal(new[] { "A3", "B3", "C3" }, blocks.Select(b => b.CellReference));
        Assert.Equal(string.Empty, blocks[0].Text);
        Assert.Contains("HYPERLINK", blocks[0].Formula);
        Assert.Equal(string.Empty, blocks[1].Formula);
        Assert.Equal("9", blocks[1].Text);
        Assert.Equal("e\u0301", blocks[2].Text);
    }

    [Theory]
    [InlineData("<row><c r=\"A1\" t=\"s\"><v>8</v></c></row>", "")]
    [InlineData("<row><c r=\"A0\"><v>1</v></c></row>", "")]
    [InlineData("<row r=\"0\"><c r=\"A1\"><v>1</v></c></row>", "")]
    [InlineData("<row r=\"invalid\"><c r=\"A1\"><v>1</v></c></row>", "")]
    [InlineData("<row r=\"1\"><c r=\"A2\"><v>1</v></c></row>", "")]
    [InlineData("<row r=\"1\"><c r=\"A1\"><v>1</v></c><c r=\"A1\"><v>2</v></c></row>", "")]
    [InlineData("", "<mergeCells><mergeCell ref=\"C4:A1\"/></mergeCells>")]
    public void InvalidSourceIndicesAndRangesAreRejected(string rows, string suffix)
    {
        using var stream = SingleSheet(Sheet(rows, suffix));
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), default);
        Assert.Throws<InvalidDataException>(() => SpreadsheetReader.Read(package, "xl/workbook.xml", default));
    }

    [Fact]
    public void PreCanceledReadDoesNotTraverseWorkbook()
    {
        using var stream = SingleSheet(Sheet(""));
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SpreadsheetReader.Read(package, "xl/workbook.xml", cancellation.Token));
    }

    private static string Sheet(string rows, string suffix = "") => $"""<worksheet xmlns="{Main}" xmlns:r="{Rel}"><sheetData>{rows}</sheetData>{suffix}</worksheet>""";
    private static string Relationships(params (string Id, string Type, string Target)[] items)
        => $"""<Relationships xmlns="{PackageRel}">""" + string.Concat(items.Select(i => $"""<Relationship Id="{i.Id}" Type="{Rel}/{i.Type}" Target="{i.Target}"/>""")) + "</Relationships>";
    private static MemoryStream SingleSheet(string sheet, Dictionary<string, string>? additional = null)
    {
        var parts = additional ?? new Dictionary<string, string>();
        parts["xl/workbook.xml"] = $"""<workbook xmlns="{Main}" xmlns:r="{Rel}"><sheets><sheet name="Data" sheetId="1" r:id="s"/></sheets></workbook>""";
        parts["xl/_rels/workbook.xml.rels"] = Relationships(("s", "worksheet", "worksheets/sheet1.xml"));
        parts["xl/worksheets/sheet1.xml"] = sheet;
        return Build(parts);
    }
    private static MemoryStream Build(Dictionary<string, string> parts)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        stream.Position = 0;
        return stream;
    }
}
