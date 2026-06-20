using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using PaddleOcrNet.Models;
using PaddleOcrNet.Structure.Table;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Validates SLANet_plus table-structure decoding (the merge_no_span_structure fix) against the real ONNX on a
/// known 4-row × 3-column grid: the structure must decode to 4 rows of 3 cells, and OCR text placed at the
/// predicted cell boxes must land in the right cells. Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TableRecognizerTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";
    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private readonly ITestOutputHelper _out;
    public TableRecognizerTests(ITestOutputHelper output) => _out = output;

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaddleOcrNet.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }

    [SkippableFact]
    public void Slanet_decodes_4x3_grid_into_12_cells_and_matches_text()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        var modelPath = Path.Combine(RepoRoot, "onnx_models", "table", "SLANet_plus.onnx");
        var imgPath = Path.Combine(RepoRoot, "test", "Assets", "synthetic_table.png");
        Skip.IfNot(File.Exists(modelPath), "SLANet_plus.onnx not present (run the export/download tooling).");
        Skip.IfNot(File.Exists(imgPath), "synthetic_table.png asset missing.");

        using var session = new InferenceSession(modelPath);
        using var recognizer = new SlanetTableRecognizer(session, Array.Empty<string>());
        using var table = Image.Load<Rgb24>(imgPath);

        // First pass with no OCR lines: validate the pure structure decode.
        var structureOnly = recognizer.Recognize(table, Array.Empty<OcrLine>());
        int rows = Regex.Matches(structureOnly.Html, "<tr>").Count;
        int cells = Regex.Matches(structureOnly.Html, "<td></td>").Count;
        _out.WriteLine($"rows={rows} cells={cells} boxes={structureOnly.CellBounds.Count}");
        _out.WriteLine(structureOnly.Html);

        Assert.Equal(4, rows);
        Assert.Equal(12, structureOnly.CellBounds.Count);

        // Second pass: place a distinct OCR line at the centre of each predicted cell box, in row-major order,
        // and assert each cell receives its own text (the matcher routes by IoU/distance).
        var lines = new List<OcrLine>();
        for (int i = 0; i < structureOnly.CellBounds.Count; i++)
        {
            var b = structureOnly.CellBounds[i];
            lines.Add(new OcrLine
            {
                Text = $"C{i}",
                Confidence = 1f,
                BoundingBox = b,
                BoundingPolygon = new[]
                {
                    new OcrPoint(b.MinX, b.MinY), new OcrPoint(b.MaxX, b.MinY),
                    new OcrPoint(b.MaxX, b.MaxY), new OcrPoint(b.MinX, b.MaxY),
                },
            });
        }

        var matched = recognizer.Recognize(table, lines);
        _out.WriteLine(matched.Html);
        // Every cell's text should appear inside a <td>…</td> in order.
        for (int i = 0; i < lines.Count; i++)
            Assert.Contains($"<td>C{i}</td>", matched.Html);
    }
}
