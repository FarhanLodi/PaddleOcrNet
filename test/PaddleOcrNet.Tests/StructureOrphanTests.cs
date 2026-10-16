using System.Reflection;
using System.Text.Json;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Structure;
using PaddleOcrNet.Structure.Layout;
using Xunit;

namespace PaddleOcrNet.Tests;

public class StructureOrphanTests
{
    [Fact]
    public void Assembly_inherits_engine_orphans_deduplicates_equivalent_caller_evidence_and_preserves_conflicts()
    {
        var source = new OcrLine { Text = "Unowned text", Confidence = 0.8, BoundingBox = new(10, 20, 100, 40),
            BoundingPolygon = new[] { new OcrPoint(10, 20), new OcrPoint(100, 20), new OcrPoint(100, 40), new OcrPoint(10, 40) } };
        source = source with { Words = new[] { new OcrWord { Text = "Unowned", Confidence = 0.8,
            BoundingBox = source.BoundingBox, BoundingPolygon = source.BoundingPolygon } } };
        var duplicate = source with { BoundingPolygon = source.BoundingPolygon.ToArray(),
            Words = source.Words.Select(w => w with { BoundingPolygon = w.BoundingPolygon.ToArray() }).ToArray() };
        var conflict = source with { Text = "Different reading" };
        var structure = new StructureResult { Blocks = Array.Empty<StructureBlock>(), OrphanLines = new[] { source } };
        var page = new DocumentPage { PageNumber = 2, Structure = structure, OrphanLines = new[] { duplicate, conflict } };
        var document = DocumentAssembler.Assemble(new[] { page });
        Assert.Equal(new[] { source, conflict }, document.Pages[0].OrphanLines);
        Assert.Same(structure, document.Pages[0].Structure);
        Assert.Equal(2, page.OrphanLines.Count); // caller collection not changed
        using var compact = JsonDocument.Parse(structure.ToJson());
        Assert.Equal("Unowned text", compact.RootElement.GetProperty("OrphanLines")[0].GetProperty("Text").GetString());
        using var full = JsonDocument.Parse(document.ToJson());
        Assert.Equal(2, full.RootElement.GetProperty("Pages")[0].GetProperty("OrphanLines").GetArrayLength());
        Assert.Equal(0.8, full.RootElement.GetProperty("Pages")[0].GetProperty("OrphanLines")[0].GetProperty("Confidence").GetDouble());
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Engine_preserves_real_ocr_readings_when_layout_assigns_only_a_vision_region()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("PADDLEOCRNET_RUN_INTEGRATION") is "1" or "true" or "TRUE",
            "Set PADDLEOCRNET_RUN_INTEGRATION=1 for the model-backed orphan regression.");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PaddleOcrNet.sln"))) root = root.Parent;
        using var image = await Image.LoadAsync<Rgb24>(Path.Combine(root!.FullName, "test", "Assets", "paddleocrnet_100_test_dataset", "057_receipts_invoices.png"));
        await using var engine = new PaddleStructureEngine(new PaddleEngineOptions(), null);
        typeof(PaddleStructureEngine).GetField("_layoutDetector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, new VisionOnlyLayout());
        typeof(PaddleStructureEngine).GetField("_layoutDetectorModel", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, LayoutModel.RtDetrL);
        var result = await engine.AnalyzeAsync(image, new StructureOptions
        {
            Languages = new[] { OcrLanguage.English }, RecognizeFormulas = false, RecognizeSeals = false, RecognizeTables = false,
        }, CancellationToken.None);
        Assert.Equal(StructureBlockType.Figure, Assert.Single(result.Blocks).Type);
        Assert.NotEmpty(result.OrphanLines);
        Assert.Contains(result.OrphanLines, l => l.Text.Length > 3);
        var document = DocumentAssembler.Assemble(new[] { new DocumentPage { PageNumber = 1, Structure = result } });
        Assert.Equal(result.OrphanLines, document.Pages[0].OrphanLines);
        using var json = JsonDocument.Parse(result.ToJson());
        Assert.Equal(result.OrphanLines.Count, json.RootElement.GetProperty("OrphanLines").GetArrayLength());
    }

    private sealed class VisionOnlyLayout : ILayoutDetector
    {
        public IReadOnlyList<LayoutRegion> Detect(Image<Rgb24> image, float scoreThreshold)
            => new[] { new LayoutRegion(StructureBlockType.Figure, new(0, 0, image.Width, image.Height), 1, 0, "image") };
        public void Dispose() { }
    }
}
