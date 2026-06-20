using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using PaddleOcrNet.Models;
using PaddleOcrNet.Structure;
using PaddleOcrNet.Structure.Export;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the Office exporters
/// <see cref="StructureDocxExporter.ToDocx"/> and <see cref="StructureXlsxExporter.ToXlsx"/>. The exporters
/// build a <c>.docx</c>/<c>.xlsx</c> by hand (a ZIP of XML parts) from a <see cref="StructureResult"/>. These
/// tests build a small hand-assembled result, then assert: (1) the ZIP carries the minimal required OOXML
/// part names with correct relationship wiring, (2) every XML part is well-formed (loads with
/// <see cref="XDocument"/>), and (3) the recovered table cell text — including merged cells — lands in the
/// right place.
/// </summary>
public class StructureOfficeExportTests
{
    private static OcrBoundingBox Box(double x1, double y1, double x2, double y2) => new(x1, y1, x2, y2);

    private static StructureResult Build(params StructureBlock[] blocks)
        => new() { Blocks = blocks, SourceWidth = 600, SourceHeight = 800 };

    // A document with a title, body text, a 2x2 table, and a formula — exercising every Word/Excel mapping.
    private static StructureResult Sample() => Build(
        new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 600, 40), Order: 0, Text: "My Document"),
        new StructureBlock(StructureBlockType.Text, Box(0, 50, 600, 120), Order: 1, Text: "Intro paragraph."),
        new StructureBlock(StructureBlockType.Title, Box(0, 130, 600, 160), Order: 2, Text: "Results"),
        new StructureBlock(StructureBlockType.Table, Box(0, 170, 600, 300), Order: 3,
            TableHtml: "<table><tr><td>Name</td><td>Score</td></tr><tr><td>Alice</td><td>97</td></tr></table>"),
        new StructureBlock(StructureBlockType.Formula, Box(0, 310, 600, 360), Order: 4, Latex: "E = mc^2"));

    // ---- ZIP / part-reading helpers --------------------------------------------------------------------

    private static IReadOnlyDictionary<string, string> ReadZip(byte[] package)
    {
        var parts = new Dictionary<string, string>();
        using var ms = new MemoryStream(package);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            using var s = entry.Open();
            using var reader = new StreamReader(s);
            parts[entry.FullName] = reader.ReadToEnd();
        }
        return parts;
    }

    private static void AssertAllPartsWellFormed(IReadOnlyDictionary<string, string> parts)
    {
        foreach (var (name, xml) in parts)
        {
            // Every emitted part is XML (.rels and .xml alike); parsing pins well-formedness.
            var ex = Record.Exception(() => XDocument.Parse(xml));
            Assert.True(ex is null, $"Part '{name}' is not well-formed XML: {ex?.Message}");
        }
    }

    // =================================================================================================
    //  DOCX
    // =================================================================================================

    [Fact]
    public void ToDocx_contains_required_parts_and_relationships()
    {
        var parts = ReadZip(Sample().ToDocx());

        Assert.Contains("[Content_Types].xml", parts.Keys);
        Assert.Contains("_rels/.rels", parts.Keys);
        Assert.Contains("word/document.xml", parts.Keys);
        Assert.Contains("word/_rels/document.xml.rels", parts.Keys);

        // The root relationship must point the package at the main document part.
        Assert.Contains("word/document.xml", parts["_rels/.rels"]);
        Assert.Contains("officeDocument", parts["_rels/.rels"]);

        // The content-type override for the main document part must be declared.
        Assert.Contains("/word/document.xml", parts["[Content_Types].xml"]);
        Assert.Contains("wordprocessingml.document.main+xml", parts["[Content_Types].xml"]);
    }

    [Fact]
    public void ToDocx_parts_are_well_formed_xml()
    {
        AssertAllPartsWellFormed(ReadZip(Sample().ToDocx()));
    }

    [Fact]
    public void ToDocx_emits_text_table_and_formula_content()
    {
        var doc = ReadZip(Sample().ToDocx())["word/document.xml"];

        Assert.Contains("My Document", doc);
        Assert.Contains("Intro paragraph.", doc);
        Assert.Contains("Results", doc);

        // A real Word table, with the recovered cell text inside it.
        Assert.Contains("<w:tbl>", doc);
        Assert.Contains("Name", doc);
        Assert.Contains("Score", doc);
        Assert.Contains("Alice", doc);
        Assert.Contains("97", doc);

        // The formula is carried as $$…$$ text (escaped; no native OMML in v1).
        Assert.Contains("E = mc^2", doc);
        Assert.Contains("$$", doc);
    }

    [Fact]
    public void ToDocx_table_honors_colspan_and_rowspan()
    {
        // Row 1: a single header spanning both columns (colspan=2).
        // Rows 2-3: a left cell spanning both rows (rowspan=2) beside two distinct right cells.
        var result = Build(new StructureBlock(StructureBlockType.Table, Box(0, 0, 600, 300), Order: 0,
            TableHtml:
                "<table>" +
                "<tr><td colspan=\"2\">Header</td></tr>" +
                "<tr><td rowspan=\"2\">Side</td><td>B1</td></tr>" +
                "<tr><td>B2</td></tr>" +
                "</table>"));

        var doc = ReadZip(result.ToDocx())["word/document.xml"];

        Assert.Contains("<w:gridSpan w:val=\"2\"/>", doc);   // colspan -> gridSpan
        Assert.Contains("w:vMerge w:val=\"restart\"", doc);  // rowspan origin
        Assert.Contains("w:vMerge w:val=\"continue\"", doc); // rowspan continuation
        Assert.Contains("Header", doc);
        Assert.Contains("Side", doc);
        Assert.Contains("B1", doc);
        Assert.Contains("B2", doc);
    }

    [Fact]
    public void ToDocx_escapes_xml_special_characters()
    {
        var result = Build(new StructureBlock(StructureBlockType.Text, Box(0, 0, 600, 40), Order: 0,
            Text: "a < b & c > d \"e\""));

        var parts = ReadZip(result.ToDocx());
        AssertAllPartsWellFormed(parts);
        Assert.Contains("a &lt; b &amp; c &gt; d", parts["word/document.xml"]);
        // The raw, unescaped form must never appear.
        Assert.DoesNotContain("a < b & c", parts["word/document.xml"]);
    }

    [Fact]
    public void SaveAsDocx_writes_a_readable_package()
    {
        var path = Path.Combine(Path.GetTempPath(), $"paddleocr_docx_{System.Guid.NewGuid():N}.docx");
        try
        {
            Sample().SaveAsDocx(path);
            Assert.True(File.Exists(path));
            var parts = ReadZip(File.ReadAllBytes(path));
            Assert.Contains("word/document.xml", parts.Keys);
            AssertAllPartsWellFormed(parts);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // =================================================================================================
    //  XLSX
    // =================================================================================================

    [Fact]
    public void ToXlsx_contains_required_parts_and_relationships()
    {
        var parts = ReadZip(Sample().ToXlsx());

        Assert.Contains("[Content_Types].xml", parts.Keys);
        Assert.Contains("_rels/.rels", parts.Keys);
        Assert.Contains("xl/workbook.xml", parts.Keys);
        Assert.Contains("xl/_rels/workbook.xml.rels", parts.Keys);
        Assert.Contains("xl/worksheets/sheet1.xml", parts.Keys);

        // Root rel -> workbook; workbook rel -> the first worksheet; both content types declared.
        Assert.Contains("xl/workbook.xml", parts["_rels/.rels"]);
        Assert.Contains("worksheets/sheet1.xml", parts["xl/_rels/workbook.xml.rels"]);
        Assert.Contains("spreadsheetml.sheet.main+xml", parts["[Content_Types].xml"]);
        Assert.Contains("/xl/worksheets/sheet1.xml", parts["[Content_Types].xml"]);

        // The workbook references its sheet via the matching r:id.
        Assert.Contains("r:id=\"rId1\"", parts["xl/workbook.xml"]);
    }

    [Fact]
    public void ToXlsx_parts_are_well_formed_xml()
    {
        AssertAllPartsWellFormed(ReadZip(Sample().ToXlsx()));
    }

    [Fact]
    public void ToXlsx_lays_out_table_cells_by_row_and_column()
    {
        var sheet = ReadZip(Sample().ToXlsx())["xl/worksheets/sheet1.xml"];

        // Header on row 1, data on row 2 (inline strings).
        Assert.Contains("Name", sheet);
        Assert.Contains("Score", sheet);
        Assert.Contains("Alice", sheet);
        Assert.Contains("97", sheet);
        Assert.Contains("t=\"inlineStr\"", sheet);

        // Cell references must be present and well-placed (A1/B1 header, A2/B2 data).
        Assert.Contains("r=\"A1\"", sheet);
        Assert.Contains("r=\"B1\"", sheet);
        Assert.Contains("r=\"A2\"", sheet);
        Assert.Contains("r=\"B2\"", sheet);
    }

    [Fact]
    public void ToXlsx_emits_one_sheet_per_table()
    {
        var result = Build(
            new StructureBlock(StructureBlockType.Table, Box(0, 0, 600, 100), Order: 0,
                TableHtml: "<table><tr><td>one</td></tr></table>"),
            new StructureBlock(StructureBlockType.Table, Box(0, 110, 600, 200), Order: 1,
                TableHtml: "<table><tr><td>two</td></tr></table>"));

        var parts = ReadZip(result.ToXlsx());

        Assert.Contains("xl/worksheets/sheet1.xml", parts.Keys);
        Assert.Contains("xl/worksheets/sheet2.xml", parts.Keys);
        Assert.Contains("one", parts["xl/worksheets/sheet1.xml"]);
        Assert.Contains("two", parts["xl/worksheets/sheet2.xml"]);
        // Two sheets declared in the workbook with distinct r:ids.
        Assert.Contains("r:id=\"rId1\"", parts["xl/workbook.xml"]);
        Assert.Contains("r:id=\"rId2\"", parts["xl/workbook.xml"]);
    }

    [Fact]
    public void ToXlsx_emits_merge_ranges_for_spanned_cells()
    {
        var result = Build(new StructureBlock(StructureBlockType.Table, Box(0, 0, 600, 300), Order: 0,
            TableHtml:
                "<table>" +
                "<tr><td colspan=\"2\">Header</td></tr>" +
                "<tr><td>A</td><td>B</td></tr>" +
                "</table>"));

        var sheet = ReadZip(result.ToXlsx())["xl/worksheets/sheet1.xml"];

        Assert.Contains("<mergeCells", sheet);
        Assert.Contains("ref=\"A1:B1\"", sheet); // the colspan=2 header merges A1 and B1
        Assert.Contains("Header", sheet);
    }

    [Fact]
    public void ToXlsx_without_tables_emits_a_single_text_sheet()
    {
        var result = Build(
            new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 600, 40), Order: 0, Text: "Heading"),
            new StructureBlock(StructureBlockType.Text, Box(0, 50, 600, 120), Order: 1, Text: "Body line."));

        var parts = ReadZip(result.ToXlsx());

        Assert.Contains("xl/worksheets/sheet1.xml", parts.Keys);
        Assert.DoesNotContain("xl/worksheets/sheet2.xml", parts.Keys);
        AssertAllPartsWellFormed(parts);

        var sheet = parts["xl/worksheets/sheet1.xml"];
        Assert.Contains("Heading", sheet);
        Assert.Contains("Body line.", sheet);
    }

    [Fact]
    public void SaveAsXlsx_writes_a_readable_package()
    {
        var path = Path.Combine(Path.GetTempPath(), $"paddleocr_xlsx_{System.Guid.NewGuid():N}.xlsx");
        try
        {
            Sample().SaveAsXlsx(path);
            Assert.True(File.Exists(path));
            var parts = ReadZip(File.ReadAllBytes(path));
            Assert.Contains("xl/workbook.xml", parts.Keys);
            AssertAllPartsWellFormed(parts);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
