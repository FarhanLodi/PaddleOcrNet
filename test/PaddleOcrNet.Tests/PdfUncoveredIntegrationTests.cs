using System.Reflection;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;
using PaddleOcrNet.Pdf;
using PaddleOcrNet.Pdf.Internal;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

[Trait("Category", "Integration")]
public class PdfUncoveredIntegrationTests
{
    private static RecognitionOptions SourceOptions => new()
    {
        UseDocOrientation = false,
        Preprocessing = new() { CorrectNonSquarePixels = false }
    };

    private static void RequireIntegration() => Skip.IfNot(
        Environment.GetEnvironmentVariable("PADDLEOCRNET_RUN_INTEGRATION") is "1" or "true" or "TRUE",
        "Set PADDLEOCRNET_RUN_INTEGRATION=1 to run real detector/recognizer selective PDF validation.");

    [SkippableFact]
    public async Task Actual_detector_regions_skip_exact_native_coverage_and_recognize_remaining_scan()
    {
        RequireIntegration();
        using var image = await LoadReceipt();
        await using var actual = new PaddleOcrService();
        var options = SourceOptions with { Region = OcrRegion.Pixels(0, 0, image.Width, image.Height) };
        var detected = await actual.DetectRegionsAsync(image, options);
        Assert.True(detected.Count >= 3, "Receipt must contain multiple real detector regions.");
        var covered = detected[0];
        var native = new OcrLine
        {
            Text = "Native region evidence", Confidence = 1,
            BoundingBox = covered.BoundingBox, BoundingPolygon = covered.BoundingPolygon
        };
        var service = DispatchProxy.Create<IPaddleOcrService, CountingProxy>();
        var proxy = (CountingProxy)(object)service;
        proxy.Target = actual;
        var result = await HybridUncoveredRecognition.RecognizeAsync(service, image,
            new[] { native }, new[] { OcrLanguage.English }, options, default);
        Assert.Equal(0, proxy.FullPageCalls);
        Assert.Equal(1, proxy.DetectionCalls);
        Assert.Equal(1, proxy.RecognitionCalls);
        Assert.InRange(proxy.Recognized.Count, 1, detected.Count - 1);
        Assert.DoesNotContain(proxy.Recognized, p => p.SequenceEqual(covered.BoundingPolygon));
        Assert.NotEmpty(result.Lines);
        Assert.All(result.Lines, line =>
        {
            Assert.InRange(line.BoundingBox.MinX, 0, image.Width);
            Assert.InRange(line.BoundingBox.MinY, 0, image.Height);
            Assert.InRange(line.BoundingBox.MaxX, 0, image.Width);
            Assert.InRange(line.BoundingBox.MaxY, 0, image.Height);
        });
        Assert.True(result.Coordinates?.SourceMappingAvailable);
        Assert.True(result.Coordinates?.RegionApplied);
    }

    [SkippableFact]
    public async Task Selective_hybrid_public_pipeline_uses_real_region_ocr_and_declares_unverified_native_text()
    {
        RequireIntegration();
        using var image = await LoadReceipt();
        await using var actual = new PaddleOcrService();
        var options = SourceOptions;
        var original = await actual.ExtractTextFromImage(image, OcrLanguage.English, options);
        Assert.True(original.Lines.Count >= 3);
        var anchor = original.Lines.Where(l => l.Text.Length >= 8 && l.Text.All(c => c < 128))
            .OrderByDescending(l => l.BoundingBox.Width).First();
        var native = anchor with { Text = "NATIVE SELECTIVE HEADER", Words = Array.Empty<OcrWord>() };
        byte[] input;
        using (var stream = new MemoryStream())
        {
            var builder = new SearchablePdfBuilder(stream);
            builder.AddPage(SearchablePdfBuilder.EncodeJpeg(image, 98), image.Width, image.Height, 72, new[] { native });
            builder.Finish();
            input = stream.ToArray();
        }
        var service = DispatchProxy.Create<IPaddleOcrService, CountingProxy>();
        var proxy = (CountingProxy)(object)service;
        proxy.Target = actual;
        var output = await service.ExtractTextFromPdfAsync(input, OcrLanguage.English, options,
            new PdfOcrOptions { Dpi = 72, TextLayer = PdfTextLayerMode.Hybrid, HybridRecognizeUncoveredOnly = true });
        var page = Assert.Single(output.Pages);
        Assert.Equal(0, proxy.FullPageCalls);
        Assert.Equal(1, proxy.DetectionCalls);
        Assert.Equal(1, proxy.RecognitionCalls);
        Assert.NotEmpty(proxy.Recognized);
        Assert.True(page.NativeTextVisualVerificationSkipped);
        Assert.Equal(PdfPageSource.Mixed, page.Source);
        Assert.Contains(page.LineEvidence, e => e.Source == PdfPageSource.EmbeddedText && e.Selected.Text == native.Text);
        Assert.Contains(page.LineEvidence, e => e.Source == PdfPageSource.Ocr && !string.IsNullOrWhiteSpace(e.Selected.Text));
        Assert.Equal("rendered-page-pixels", page.CoordinateFrame);
        Assert.True(page.Ocr.Coordinates?.SourceMappingAvailable);
    }

    private static async Task<Image<Rgb24>> LoadReceipt() => await Image.LoadAsync<Rgb24>(Path.Combine(
        OcrDatasetBenchmarkTests.RepoRoot, "test", "Assets", "paddleocrnet_100_test_dataset", "057_receipts_invoices.png"));

    public class CountingProxy : DispatchProxy
    {
        public IPaddleOcrService Target { get; set; } = null!;
        public int FullPageCalls { get; private set; }
        public int DetectionCalls { get; private set; }
        public int RecognitionCalls { get; private set; }
        public IReadOnlyList<IReadOnlyList<OcrPoint>> Recognized { get; private set; } = Array.Empty<IReadOnlyList<OcrPoint>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IPaddleOcrService.ExtractTextFromImage)) FullPageCalls++;
            if (targetMethod.Name == nameof(IPaddleOcrService.DetectRegionsAsync)) DetectionCalls++;
            if (targetMethod.Name == nameof(IPaddleOcrService.RecognizeRegionsAsync))
            {
                RecognitionCalls++;
                Recognized = ((IEnumerable<IReadOnlyList<OcrPoint>>)args![1]!).ToArray();
            }
            return targetMethod.Invoke(Target, args);
        }
    }
}
