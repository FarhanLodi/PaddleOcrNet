using Microsoft.ML.OnnxRuntime;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using PaddleOcrNet.Structure;
using PaddleOcrNet.Structure.Table;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// End-to-end guards for the multi-line-cell and paragraph-spacing fixes, run against the real models. The
/// unit suites pin the rules in isolation (<see cref="TableCellLineBreakTests"/>,
/// <see cref="MultiLineCellExportTests"/>, <see cref="FullTextGroupingTests"/>); these confirm the same
/// behaviour survives the actual SLANet graph and the full structure pipeline, which is where the reported
/// regression was visible. Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MultiLineCellRegressionTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";
    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private readonly ITestOutputHelper _out;
    public MultiLineCellRegressionTests(ITestOutputHelper output) => _out = output;

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

    /// <summary>
    /// Two OCR lines stacked inside one predicted cell must come out as <c>text&lt;br&gt;text</c> rather than
    /// space-joined onto a single line. Drives the real SLANet graph so the whole match → group → HTML path is
    /// exercised, not just the grouping helper.
    /// </summary>
    [SkippableFact]
    public void Stacked_lines_in_a_predicted_cell_are_separated_by_a_break()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        var modelPath = Path.Combine(RepoRoot, "onnx_models", "table", "SLANet_plus.onnx");
        var imgPath = Path.Combine(RepoRoot, "test", "Assets", "synthetic_table.png");
        Skip.IfNot(File.Exists(modelPath), "SLANet_plus.onnx not present (run the export/download tooling).");
        Skip.IfNot(File.Exists(imgPath), "synthetic_table.png asset missing.");

        using var session = new InferenceSession(modelPath);
        using var recognizer = new SlanetTableRecognizer(session, Array.Empty<string>());
        using var table = Image.Load<Rgb24>(imgPath);

        var cells = recognizer.Recognize(table, Array.Empty<OcrLine>()).CellBounds;
        Assert.NotEmpty(cells);

        // Two non-overlapping lines per cell: the top and bottom thirds of the predicted box. Each still
        // overlaps its own cell far more than any neighbour, so the matcher keeps them together.
        var lines = new List<OcrLine>();
        for (int i = 0; i < cells.Count; i++)
        {
            var b = cells[i];
            double third = b.Height / 3.0;
            lines.Add(Line($"T{i}", b.MinX, b.MinY, b.MaxX, b.MinY + third));
            lines.Add(Line($"B{i}", b.MinX, b.MaxY - third, b.MaxX, b.MaxY));
        }

        var html = recognizer.Recognize(table, lines).Html;
        _out.WriteLine(html);

        for (int i = 0; i < cells.Count; i++)
        {
            Assert.Contains($"T{i}<br>B{i}", html);
        }
    }

    /// <summary>
    /// The reported document (a Russian editorial grid of multi-line boxes) through the full pipeline: the
    /// recovered table must keep its intra-cell line breaks, the Markdown must not carry the recognizer's
    /// <c>&lt;html&gt;/&lt;body&gt;</c> wrapper, the HTML export must keep the table as markup rather than
    /// degrading it to fallback text, and paragraph grouping must put a blank line between blocks.
    /// </summary>
    [SkippableFact]
    public async Task Reported_document_keeps_cell_breaks_and_paragraph_spacing()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        var imgPath = Path.Combine(RepoRoot, "test", "Assets", "GitHug_TestImage.png");
        Skip.IfNot(File.Exists(imgPath), "GitHug_TestImage.png asset missing.");

        await using var ocr = new PaddleOcrService();

        // ---- paragraph grouping puts a blank line between blocks ----
        var text = await ocr.ExtractTextFromImage(
            imgPath,
            [OcrLanguage.Russian, OcrLanguage.English],
            RecognitionOptions.Default with { Grouping = TextGrouping.Paragraph });

        Assert.True(text.Lines.Count > 1, "expected several paragraph blocks");
        Assert.Contains("\n\n", text.FullText);

        // Line grouping must NOT gain the blank lines — the separator is grouping-dependent.
        var lineGrouped = await ocr.ExtractTextFromImage(
            imgPath,
            [OcrLanguage.Russian, OcrLanguage.English],
            RecognitionOptions.Default with { Grouping = TextGrouping.Line });

        Assert.DoesNotContain("\n\n", lineGrouped.FullText);

        // ---- structure export keeps the cell breaks and drops the wrapper ----
        var doc = await ocr.AnalyzeDocumentAsync(imgPath, StructureOptions.Default with
        {
            Languages = [OcrLanguage.Russian, OcrLanguage.English],
            TableModel = TableRecognitionModel.SlaNeXt,
            LayoutModel = LayoutModel.RtDetrL,
        });

        var markdown = doc.ToMarkdown();
        _out.WriteLine(markdown);

        Skip.If(doc.Blocks.All(b => b.Type != StructureBlockType.Table),
            "layout produced no table region for this page; nothing to assert about cell breaks.");

        Assert.Contains("<br>", markdown);
        Assert.DoesNotContain("<html>", markdown);
        Assert.DoesNotContain("<body>", markdown);

        var html = doc.ToHtml();
        Assert.Contains("<table>", html);
        // Exactly one <body>: the exporter's own. A nested one would mean the wrapper leaked through.
        Assert.Equal(1, CountOf(html, "<body"));
    }

    private static OcrLine Line(string text, double x1, double y1, double x2, double y2) => new()
    {
        Text = text,
        Confidence = 1f,
        BoundingBox = new OcrBoundingBox(x1, y1, x2, y2),
        BoundingPolygon = new[]
        {
            new OcrPoint(x1, y1), new OcrPoint(x2, y1),
            new OcrPoint(x2, y2), new OcrPoint(x1, y2),
        },
    };

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
