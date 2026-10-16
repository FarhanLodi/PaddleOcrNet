using Docnet.Core;
using Docnet.Core.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;
using PaddleOcrNet.Pdf;
using PaddleOcrNet.Pdf.Internal;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

[Trait("Category", "Integration")]
public class PdfHybridIntegrationTests
{
    [SkippableFact]
    public async Task Native_header_over_real_scan_preserves_missing_text_conflicts_and_searchable_geometry()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("PADDLEOCRNET_RUN_INTEGRATION") is "1" or "true" or "TRUE",
            "Set PADDLEOCRNET_RUN_INTEGRATION=1 to run model-backed hybrid PDF validation.");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PaddleOcrNet.sln"))) root = root.Parent;
        using var image = await Image.LoadAsync<Rgb24>(Path.Combine(root!.FullName, "test", "Assets", "paddleocrnet_100_test_dataset", "057_receipts_invoices.png"));
        await using var service = new PaddleOcrService();
        var baseline = await service.ExtractTextFromImage(image, OcrLanguage.English);
        Assert.True(baseline.Lines.Count >= 3);
        var anchor = baseline.Lines.Where(l => l.Text.Length >= 8 && l.Text.All(c => c < 128)).OrderByDescending(l => l.BoundingBox.Width).First();
        // A deliberately wrong yet valid native reading overlays actual visual text, a common stale-OCR-layer failure.
        var conflicting = anchor with { Text = new string('X', anchor.Text.Length), Words = Array.Empty<OcrWord>() };
        byte[] input;
        using (var stream = new MemoryStream())
        {
            var builder = new SearchablePdfBuilder(stream);
            builder.AddPage(SearchablePdfBuilder.EncodeJpeg(image, 98), image.Width, image.Height, 72, new[] { conflicting, conflicting });
            builder.Finish();
            input = stream.ToArray();
        }
        var (result, searchable) = await service.CreateSearchablePdfAsync(input, OcrLanguage.English,
            pdfOptions: new PdfOcrOptions { Dpi = 72, TextLayer = PdfTextLayerMode.Hybrid });
        var page = Assert.Single(result.Pages);
        Assert.Equal(PdfPageSource.Mixed, page.Source);
        var native = Assert.Single(page.LineEvidence, e => e.Source == PdfPageSource.EmbeddedText);
        Assert.Equal(conflicting.Text, native.Selected.Text);
        Assert.True(native.HasConflict);
        Assert.NotEmpty(native.Alternatives);
        Assert.Contains(page.LineEvidence, e => e.Source == PdfPageSource.Ocr && !string.IsNullOrWhiteSpace(e.Selected.Text));
        Assert.Equal(page.Ocr.Lines.Count, page.Ocr.Lines.Select(l => (l.Text, l.BoundingBox)).Distinct().Count());

        using var reader = DocLib.Instance.GetDocReader(searchable, new PageDimensions(1.0));
        using var rendered = reader.GetPageReader(0);
        Assert.Equal(1, rendered.GetText().Split(conflicting.Text, StringSplitOptions.None).Length - 1);
        var glyphs = rendered.GetCharacters().Where(c => !char.IsWhiteSpace(c.Char) && !char.IsControl(c.Char)).ToArray();
        Assert.NotEmpty(glyphs);
        double aligned = glyphs.Count(g => page.Ocr.Lines.Any(l =>
            (g.Box.Left + g.Box.Right) / 2.0 >= l.BoundingBox.MinX - 3 &&
            (g.Box.Left + g.Box.Right) / 2.0 <= l.BoundingBox.MaxX + 3 &&
            (g.Box.Top + g.Box.Bottom) / 2.0 >= l.BoundingBox.MinY - l.BoundingBox.Height * 0.25 &&
            (g.Box.Top + g.Box.Bottom) / 2.0 <= l.BoundingBox.MaxY + l.BoundingBox.Height * 0.25)) / (double)glyphs.Length;
        Assert.True(aligned >= 0.95, $"Only {aligned:P1} of selectable glyphs align with selected regions.");
    }
}
