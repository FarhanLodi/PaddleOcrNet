using System.IO.Compression;
using System.Text.Json;
using PaddleOcrNet.Models;
using PaddleOcrNet.Office;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

[Trait("Category", "Integration")]
public sealed class OfficeOcrIntegrationTests
{
    [SkippableFact]
    public async Task EmbeddedReceiptOcrMatchesDirectImageAndPreservesNativeTextAndGeometry()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("PADDLEOCRNET_RUN_INTEGRATION") is "1" or "true" or "TRUE",
            "Set PADDLEOCRNET_RUN_INTEGRATION=1 for model-backed Office embedded-image OCR.");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PaddleOcrNet.sln"))) root = root.Parent;
        string path = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "test", "Assets", "paddleocrnet_100_test_dataset", "057_receipts_invoices.png");
        byte[] pixels = await File.ReadAllBytesAsync(path);
        using var package = OfficeInputTests.Package(image: true);
        using (var zip = new ZipArchive(package, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry("word/media/picture.png")!.Delete();
            using var image = zip.CreateEntry("word/media/picture.png").Open();
            image.Write(pixels);
        }
        package.Position = 0;
        await using var service = new PaddleOcrService(new PaddleOcrServiceOptions { ExecutionProvider = OcrExecutionProvider.Cpu });
        var baseline = await service.ExtractTextFromImage(pixels, OcrLanguage.English);
        Assert.True(baseline.Lines.Count >= 3);
        Assert.False(string.IsNullOrWhiteSpace(baseline.FullText));
        var document = await service.ExtractTextFromOfficeAsync(package,
            new OfficeDocumentOptions { Languages = [OcrLanguage.English] });
        Assert.Contains("Native text", document.FullText);
        var imageBlock = Assert.Single(document.Sections.SelectMany(s => s.Blocks), b => b.Kind == OfficeContentKind.Image);
        var imageOcr = Assert.IsType<OcrResult>(imageBlock.ImageOcr);
        Assert.Equal(baseline.FullText, imageOcr.FullText);
        Assert.Equal(baseline.FullText, imageBlock.Text);
        Assert.Equal(baseline.SourceWidth, imageOcr.SourceWidth);
        Assert.Equal(baseline.SourceHeight, imageOcr.SourceHeight);
        Assert.Equal(baseline.Lines.Select(l => (l.Text, l.BoundingBox)), imageOcr.Lines.Select(l => (l.Text, l.BoundingBox)));
        var restored = JsonSerializer.Deserialize(document.ToJson(), PaddleOcrJsonContext.Default.OfficeDocumentResult)!;
        var serializedImage = Assert.Single(restored.Sections.SelectMany(s => s.Blocks), b => b.Kind == OfficeContentKind.Image);
        Assert.Equal("word/media/picture.png", serializedImage.ImagePart);
        Assert.Equal(baseline.FullText, serializedImage.ImageOcr!.FullText);
        Assert.Equal(baseline.Lines.Select(l => l.BoundingBox), serializedImage.ImageOcr.Lines.Select(l => l.BoundingBox));
    }
}
