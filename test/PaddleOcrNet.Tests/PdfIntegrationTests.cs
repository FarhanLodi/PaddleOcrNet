using Docnet.Core;
using Docnet.Core.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;
using PaddleOcrNet.Pdf;
using PaddleOcrNet.Pdf.Internal;
using PaddleOcrNet.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-dependent PDF tests: compare a born-digital page's embedded text layer with OCR of the same page, and prove
/// that a searchable PDF built from CJK and non-Latin-1 Latin images round-trips through PDFium at the OCR boxes.
/// Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public class PdfIntegrationTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";

    private readonly ITestOutputHelper _output;

    public PdfIntegrationTests(ITestOutputHelper output) => _output = output;

    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

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

    [SkippableTheory]
    [InlineData("invoice_778899.pdf")]
    [InlineData("Monthly_Report_With_Image.pdf")]
    [InlineData("Quantum_Harvest_Magazine_Article.pdf")]
    public async Task Embedded_text_layer_agrees_with_ocr_of_the_rendered_page(string file)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run (downloads models).");

        string pdf = Path.Combine(RepoRoot, "test", "Assets", "pdf", file);
        await using var service = new PaddleOcrService();

        var ocr = await service.ExtractTextFromPdfAsync(pdf, OcrLanguage.English);
        var embedded = await service.ExtractTextFromPdfAsync(pdf, OcrLanguage.English, pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto });

        var ocrPage = Assert.Single(ocr.Pages);
        var embeddedPage = Assert.Single(embedded.Pages);
        Assert.Equal(PdfPageSource.Ocr, ocrPage.Source);
        Assert.Equal(PdfPageSource.EmbeddedText, embeddedPage.Source);
        Assert.Equal(ocrPage.PixelWidth, embeddedPage.PixelWidth);
        Assert.Equal(ocrPage.PixelHeight, embeddedPage.PixelHeight);

        double similarity = Similarity(Normalize(ocrPage.Ocr.FullText), Normalize(embeddedPage.Ocr.FullText));

        // For each embedded line, the best-overlapping OCR line.
        var ious = embeddedPage.Ocr.Lines
            .Select(e => ocrPage.Ocr.Lines.Select(o => IoU(e.BoundingBox, o.BoundingBox)).DefaultIfEmpty(0).Max())
            .ToList();
        double meanIoU = ious.Average();
        double matched = ious.Count(v => v >= 0.5) / (double)ious.Count;

        _output.WriteLine($"{file}: ocrLines={ocrPage.Ocr.Lines.Count} embeddedLines={embeddedPage.Ocr.Lines.Count} " +
            $"similarity={similarity:F3} meanIoU={meanIoU:F3} linesIoU>=0.5={matched:P0} " +
            $"ocr={ocrPage.Ocr.Duration.TotalMilliseconds:F0}ms embedded={embeddedPage.Ocr.Duration.TotalMilliseconds:F1}ms");

        Assert.True(similarity >= 0.85, $"text similarity {similarity:F3}");
        Assert.True(meanIoU >= 0.5, $"mean IoU {meanIoU:F3}");
    }

    [SkippableTheory]
    [InlineData("lang_zh_typed.png", "ch")]
    [InlineData("paddleocrnet_100_test_dataset/057_receipts_invoices.png", "en")]
    public async Task Searchable_pdf_text_round_trips_at_the_ocr_boxes(string imageFile, string language)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run (downloads models).");

        // Wrap the image in an image-only PDF at 72 DPI, so page points equal image pixels, then OCR it at 72 DPI.
        byte[] imagePdf;
        using (var image = await Image.LoadAsync<Rgb24>(Path.Combine(RepoRoot, "test", "Assets", imageFile)))
        using (var ms = new MemoryStream())
        {
            var builder = new SearchablePdfBuilder(ms);
            builder.AddPage(SearchablePdfBuilder.EncodeJpeg(image, 95), image.Width, image.Height, 72, Array.Empty<OcrLine>());
            builder.Finish();
            imagePdf = ms.ToArray();
        }

        await using var service = new PaddleOcrService();
        var (result, searchable) = await service.CreateSearchablePdfAsync(
            imagePdf, OcrLanguageExtensions.FromCode(language), pdfOptions: new PdfOcrOptions { Dpi = 72 });

        var lines = Assert.Single(result.Pages).Ocr.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        Assert.NotEmpty(lines);

        using var reader = DocLib.Instance.GetDocReader(searchable, new PageDimensions(1.0));
        using var page = reader.GetPageReader(0);
        string extracted = Strip(page.GetText());

        var missing = lines.Where(l => !extracted.Contains(Strip(l.Text), StringComparison.Ordinal)).ToList();
        foreach (var l in missing) _output.WriteLine($"missing: {l.Text}");

        // Characters beyond Latin-1 (CJK, ₹, em dash) must survive; the old WinAnsi layer turned them into '?'.
        var nonLatin1 = lines.SelectMany(l => l.Text).Where(c => c > 0xFF && !char.IsWhiteSpace(c)).Distinct().ToList();
        Assert.All(nonLatin1, c => Assert.Contains(c.ToString(), extracted));

        var glyphs = page.GetCharacters().Where(c => !char.IsWhiteSpace(c.Char) && !char.IsControl(c.Char)).ToList();
        int aligned = glyphs.Count(g =>
        {
            double cx = (g.Box.Left + g.Box.Right) / 2.0, cy = (g.Box.Top + g.Box.Bottom) / 2.0;
            return lines.Any(l => cx >= l.BoundingBox.MinX - 3 && cx <= l.BoundingBox.MaxX + 3
                && cy >= l.BoundingBox.MinY - 0.25 * l.BoundingBox.Height && cy <= l.BoundingBox.MaxY + 0.25 * l.BoundingBox.Height);
        });
        double alignedRatio = aligned / (double)glyphs.Count;

        _output.WriteLine($"{imageFile}: lines={lines.Count} missing={missing.Count} nonLatin1={new string(nonLatin1.ToArray())} " +
            $"glyphs={glyphs.Count} alignedToOcrBox={alignedRatio:P1}");

        Assert.Empty(missing);
        Assert.True(alignedRatio >= 0.95, $"only {alignedRatio:P1} of glyph boxes sit on an OCR box");
    }

    private static string Strip(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1.0 - prev[b.Length] / (double)Math.Max(a.Length, b.Length);
    }

    private static double IoU(OcrBoundingBox a, OcrBoundingBox b)
    {
        double w = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
        double h = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
        if (w <= 0 || h <= 0) return 0;
        double inter = w * h;
        return inter / (a.Width * a.Height + b.Width * b.Height - inter);
    }
}
