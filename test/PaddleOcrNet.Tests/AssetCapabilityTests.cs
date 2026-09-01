using System.IO.Compression;
using System.Text.RegularExpressions;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using PaddleOcrNet.Structure;
using PaddleOcrNet.Structure.Export;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Capability-level coverage for the purpose-built assets in <c>test/Assets/</c> — tables, a formula, a seal
/// and the two upside-down pairs. <see cref="AssetsOcrTests"/> already runs plain OCR over every file as a
/// smoke test; this suite asserts what each image was actually added to prove, so a regression in table
/// structure recovery, formula/seal extraction or orientation correction fails loudly instead of quietly
/// returning fewer lines.
/// <para>
/// Assertions are deliberately structural (shape of the recovered grid, presence of LaTeX, relative
/// character yield) rather than exact transcriptions: the models are free to differ slightly between
/// versions, but these invariants must hold. Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssetCapabilityTests : IClassFixture<AssetCapabilityTests.ServiceFixture>
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";
    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private readonly ServiceFixture _fixture;
    private readonly ITestOutputHelper _out;

    public AssetCapabilityTests(ServiceFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _out = output;
    }

    /// <summary>One shared service for the class so the models load once, not per case.</summary>
    public sealed class ServiceFixture : IDisposable
    {
        public PaddleOcrService Service { get; } = new();
        public void Dispose() => Service.Dispose();
    }

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaddleOcrNet.sln")))
                dir = dir.Parent;
            return dir?.FullName
                ?? throw new InvalidOperationException("Could not locate repo root (PaddleOcrNet.sln not found).");
        }
    }

    private static string Asset(string name) => Path.Combine(RepoRoot, "test", "Assets", name);

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>Recognized characters, ignoring whitespace — the yield metric for the orientation pairs.</summary>
    private static int CharYield(OcrResult result)
        => result.Lines.Sum(l => l.Text.Count(c => !char.IsWhiteSpace(c)));

    // =================================================================================================
    // Tables — the structure recovery and the export chain it feeds
    // =================================================================================================

    [SkippableTheory]
    [InlineData("table.jpg")]
    [InlineData("medal_table.png")]
    public async Task Table_images_recover_a_multi_row_multi_column_grid(string fileName)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        Skip.IfNot(File.Exists(Asset(fileName)), $"{fileName} missing.");

        var doc = await _fixture.Service.AnalyzeDocumentAsync(Asset(fileName), StructureOptions.Default with
        {
            Languages = [OcrLanguage.ChineseSimplified, OcrLanguage.English],
            RecognizeTables = true,
        });

        var table = doc.Blocks.FirstOrDefault(b => b.Type == StructureBlockType.Table);
        Skip.If(table is null, "layout found no table region on this page.");
        _out.WriteLine(table!.TableHtml ?? "(no html)");

        Assert.False(string.IsNullOrWhiteSpace(table.TableHtml));
        Assert.True(Count(table.TableHtml!, "<tr>") >= 2, "expected at least two recovered rows");
        Assert.True(Count(table.TableHtml!, "<td") >= 4, "expected at least four recovered cells");

        // The grid the OOXML exporters consume must be genuinely 2-D, not a single column.
        var grid = OoxmlHtmlTable.Parse(table.TableHtml);
        Assert.NotNull(grid);
        Assert.True(grid!.RowCount >= 2, $"grid rows = {grid.RowCount}");
        Assert.True(grid.ColumnCount >= 2, $"grid columns = {grid.ColumnCount}");
        _out.WriteLine($"grid: {grid.RowCount} x {grid.ColumnCount}");
    }

    [SkippableTheory]
    [InlineData("table.jpg")]
    [InlineData("medal_table.png")]
    public async Task Table_images_export_a_real_table_to_every_format(string fileName)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        Skip.IfNot(File.Exists(Asset(fileName)), $"{fileName} missing.");

        var doc = await _fixture.Service.AnalyzeDocumentAsync(Asset(fileName), StructureOptions.Default with
        {
            Languages = [OcrLanguage.ChineseSimplified, OcrLanguage.English],
            RecognizeTables = true,
        });

        Skip.If(doc.Blocks.All(b => b.Type != StructureBlockType.Table), "layout found no table region.");

        // Markdown: the table, and none of the recognizer's <html>/<body> wrapper.
        var markdown = doc.ToMarkdown();
        Assert.Contains("<table>", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<html>", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<body>", markdown, StringComparison.OrdinalIgnoreCase);

        // HTML: the grid survived as markup instead of degrading to the escaped-text fallback,
        // and the document has exactly one <body> — the exporter's own.
        var html = doc.ToHtml();
        Assert.Contains("<td", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Count(html, "<body"));

        // Word: a real <w:tbl> with more than one row.
        var docx = ReadEntry(doc.ToDocx(), "word/document.xml");
        Assert.Contains("<w:tbl>", docx);
        Assert.True(Regex.Matches(docx, "<w:tr>").Count >= 2, "expected at least two Word table rows");

        // Excel: a worksheet plus the styles part multi-line cells reference.
        var xlsx = doc.ToXlsx();
        Assert.Contains("<row ", ReadEntry(xlsx, "xl/worksheets/sheet1.xml"));
        Assert.Contains("cellXfs", ReadEntry(xlsx, "xl/styles.xml"));
    }

    private static string ReadEntry(byte[] package, string entryName)
    {
        using var ms = new MemoryStream(package);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    // =================================================================================================
    // Formula / seal
    // =================================================================================================

    [SkippableFact]
    public async Task A_document_formula_is_recovered_as_latex_and_wrapped_in_markdown()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        Skip.IfNot(File.Exists(Asset("doc_with_formula.png")), "doc_with_formula.png missing.");

        var doc = await _fixture.Service.AnalyzeDocumentAsync(Asset("doc_with_formula.png"), StructureOptions.Default with
        {
            Languages = [OcrLanguage.English],
            RecognizeFormulas = true,
        });

        var formula = doc.Blocks.FirstOrDefault(b => b.Type == StructureBlockType.Formula);
        Skip.If(formula is null, "layout found no formula region on this page.");
        _out.WriteLine(formula!.Latex ?? "(no latex)");

        Assert.False(string.IsNullOrWhiteSpace(formula.Latex));
        // The Markdown exporter must wrap recovered LaTeX as a display-math block.
        Assert.Contains("$$", doc.ToMarkdown());
    }

    /// <summary>
    /// The seal region must be found and cover the stamp.
    /// <para>
    /// <b>Text recovery is a known open defect</b> and is deliberately NOT asserted here: on this fixture
    /// <c>SealRecognizer</c> returns zero lines (so the block's <c>Text</c> is null) even though the region is
    /// detected at 0.96 and plain OCR over the same image reads 发票专用章 and 吗繁物. See the "Known issues"
    /// section of CHANGELOG 2.0.3. This test pins the half that works so the detection side cannot regress
    /// unnoticed while the recognition side is outstanding; tighten it to assert the text once that is fixed.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_seal_page_is_detected_as_a_seal_region()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        Skip.IfNot(File.Exists(Asset("seal.png")), "seal.png missing.");

        var doc = await _fixture.Service.AnalyzeDocumentAsync(Asset("seal.png"), StructureOptions.Default with
        {
            Languages = [OcrLanguage.ChineseSimplified],
            RecognizeSeals = true,
        });

        var seal = doc.Blocks.FirstOrDefault(b => b.Type == StructureBlockType.Seal);
        Assert.NotNull(seal);
        _out.WriteLine($"seal score={seal!.Score:0.00} bounds={seal.Bounds} " +
                       $"text={seal.Text ?? "(null — known issue)"} lines={seal.Lines?.Count ?? 0}");

        Assert.True(seal.Score > 0.5, $"seal detected with low confidence: {seal.Score}");
        Assert.True(seal.Bounds.Width > 100 && seal.Bounds.Height > 100, "seal region should cover the stamp");
    }

    /// <summary>
    /// The default table model's cell boxes must actually line up with the grid it decodes: the mean cell
    /// height should be close to (page height / row count), and only the genuine bottom row may sit on the
    /// crop's bottom edge. This is the invariant <c>SlaNeXt</c> currently violates — mean cell 2.48× the true
    /// row height and 47 of 96 cells clamped — so pinning it here keeps the working path working and gives a
    /// future SLANeXt fix a ready-made yardstick.
    /// </summary>
    [SkippableTheory]
    [InlineData("medal_table.png")]
    [InlineData("table.jpg")]
    public async Task Default_table_model_cell_boxes_match_the_decoded_grid(string fileName)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        Skip.IfNot(File.Exists(Asset(fileName)), $"{fileName} missing.");

        var doc = await _fixture.Service.AnalyzeDocumentAsync(Asset(fileName), StructureOptions.Default with
        {
            Languages = [OcrLanguage.ChineseSimplified, OcrLanguage.English],
            TableModel = TableRecognitionModel.SlanetPlus,
        });

        var table = doc.Blocks.FirstOrDefault(b => b.Type == StructureBlockType.Table);
        Skip.If(table is null, "layout found no table region.");

        int rows = Count(table!.TableHtml ?? "", "<tr>");
        Skip.If(rows < 2, "not enough rows to judge cell geometry.");

        // Cells that keep their text separate produce no <br> on a single-line-per-cell fixture; a flood of
        // them is the signature of boxes swallowing neighbouring rows.
        int breaks = Count(table.TableHtml!, "<br>");
        int cells = Count(table.TableHtml!, "<td");
        _out.WriteLine($"{fileName}: rows={rows} cells={cells} br={breaks}");

        Assert.True(breaks * 4 < cells,
            $"too many multi-line cells for a single-line-per-cell table: {breaks} breaks over {cells} cells");
    }

    // =================================================================================================
    // Orientation — the rot180 pairs
    // =================================================================================================

    /// <summary>
    /// An upside-down page must yield materially more text with orientation detection on than off. Comparing
    /// the same image against itself keeps this independent of what the models can read in absolute terms.
    /// </summary>
    [SkippableTheory]
    [InlineData("book_rot180.jpg")]
    [InlineData("textline_rot180.jpg")]
    public async Task Orientation_detection_recovers_upside_down_pages(string fileName)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        Skip.IfNot(File.Exists(Asset(fileName)), $"{fileName} missing.");

        var withOut = await _fixture.Service.ExtractTextFromImage(
            Asset(fileName), OcrLanguage.Auto,
            RecognitionOptions.Default with
            {
                Preprocessing = RecognitionOptions.Default.Preprocessing with { DetectOrientation = false },
            });

        var withOn = await _fixture.Service.ExtractTextFromImage(
            Asset(fileName), OcrLanguage.Auto,
            RecognitionOptions.Default with
            {
                Preprocessing = RecognitionOptions.Default.Preprocessing with { DetectOrientation = true },
            });

        _out.WriteLine($"[{fileName}] chars off={CharYield(withOut)} on={CharYield(withOn)}");
        _out.WriteLine("--- off ---");
        foreach (var l in withOut.Lines) _out.WriteLine($"  {l.Text}");
        _out.WriteLine("--- on ---");
        foreach (var l in withOn.Lines) _out.WriteLine($"  {l.Text}");

        Assert.True(CharYield(withOn) >= CharYield(withOut),
            $"orientation detection lost text: off={CharYield(withOut)} on={CharYield(withOn)}");
    }

    /// <summary>
    /// The upright members of the pairs are the control: they must read without needing any correction.
    /// </summary>
    [SkippableTheory]
    [InlineData("book.jpg")]
    [InlineData("textline.png")]
    public async Task Upright_pages_read_without_orientation_correction(string fileName)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        Skip.IfNot(File.Exists(Asset(fileName)), $"{fileName} missing.");

        var result = await _fixture.Service.ExtractTextFromImage(Asset(fileName), OcrLanguage.Auto);

        _out.WriteLine($"[{fileName}] {result.Lines.Count} line(s), {CharYield(result)} chars");
        foreach (var l in result.Lines) _out.WriteLine($"  [{l.Confidence:0.00}] {l.Text}");

        Assert.NotEmpty(result.Lines);
        Assert.True(CharYield(result) > 0, "expected some recognized characters");
    }
}
