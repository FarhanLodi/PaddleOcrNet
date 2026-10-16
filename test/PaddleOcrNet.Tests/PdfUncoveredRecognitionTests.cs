using System.Reflection;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;
using PaddleOcrNet.Pdf;
using PaddleOcrNet.Pdf.Internal;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

public sealed class PdfUncoveredRecognitionTests
{
    private static RecognitionOptions SourceOptions => new()
    {
        UseDocOrientation = false,
        Preprocessing = new PreprocessingOptions { CorrectNonSquarePixels = false }
    };

    private static OcrLine Line(string text, double y = 0, double width = 100, double height = 10) => new()
    {
        Text = text, Confidence = .9, BoundingBox = new(0, y, width, y + height),
        BoundingPolygon = new[] { new OcrPoint(0, y), new OcrPoint(width, y), new OcrPoint(width, y + height), new OcrPoint(0, y + height) }
    };

    [Fact]
    public async Task Only_uncovered_regions_are_recognized_and_languages_options_are_preserved()
    {
        using var image = new Image<Rgb24>(200, 100);
        var service = DispatchProxy.Create<IPaddleOcrService, SelectiveProxy>();
        var proxy = (SelectiveProxy)(object)service;
        var header = Line("native header");
        var body = Line("scanned body", 30);
        proxy.Detected = new[] { Region(header), Region(body) };
        var options = SourceOptions with { Region = OcrRegion.Pixels(0, 0, 180, 80) };
        var languages = new[] { OcrLanguage.English };
        await HybridUncoveredRecognition.RecognizeAsync(service, image, new[] { header }, languages, options, default);
        Assert.Equal(0, proxy.FullPageCalls);
        Assert.Equal(1, proxy.DetectionCalls);
        Assert.Equal(1, proxy.RegionCalls);
        Assert.Equal(body.BoundingPolygon, Assert.Single(proxy.Recognized));
        Assert.Same(options, proxy.DetectionOptions);
        Assert.Same(options, proxy.RecognitionOptions);
        Assert.Same(languages, proxy.Languages);
    }

    [Fact]
    public async Task Partial_native_coverage_never_suppresses_larger_detected_region()
    {
        using var image = new Image<Rgb24>(200, 100);
        var service = DispatchProxy.Create<IPaddleOcrService, SelectiveProxy>();
        var proxy = (SelectiveProxy)(object)service;
        var whole = Line("full text", width: 100, height: 40);
        proxy.Detected = new[] { Region(whole) };
        await HybridUncoveredRecognition.RecognizeAsync(service, image, new[] { Line("small header") }, new[] { OcrLanguage.English }, SourceOptions, default);
        Assert.Equal(whole.BoundingPolygon, Assert.Single(proxy.Recognized));
    }

    [Fact]
    public void Native_coverage_uses_union_and_rejects_oversized_native_containment()
    {
        var detected = new OcrBoundingBox(0, 0, 100, 20);
        var top = Line("top");
        Assert.False(HybridUncoveredRecognition.Covered(detected, new[] { top, top, top }));
        Assert.True(HybridUncoveredRecognition.Covered(detected, new[] { top, Line("bottom", 10) }));
        Assert.False(HybridUncoveredRecognition.Covered(detected, new[] { Line("giant paragraph", width: 200, height: 100) }));
    }

    [Fact]
    public async Task Invalid_native_text_does_not_skip_visual_recognition()
    {
        using var image = new Image<Rgb24>(200, 100);
        var service = DispatchProxy.Create<IPaddleOcrService, SelectiveProxy>();
        var proxy = (SelectiveProxy)(object)service;
        proxy.Detected = new[] { Region(Line("text")) };
        await HybridUncoveredRecognition.RecognizeAsync(service, image, new[] { Line("broken\ufffd") }, new[] { OcrLanguage.English }, SourceOptions, default);
        Assert.Single(proxy.Recognized);
    }

    [Fact]
    public async Task Out_of_region_native_and_detector_boxes_do_not_expand_caller_scope()
    {
        using var image = new Image<Rgb24>(200, 100);
        var service = DispatchProxy.Create<IPaddleOcrService, SelectiveProxy>();
        var proxy = (SelectiveProxy)(object)service;
        proxy.Detected = new[] { Region(Line("outside")), Region(Line("inside", 30)) };
        var options = SourceOptions with { Region = OcrRegion.Pixels(0, 20, 150, 60) };
        await HybridUncoveredRecognition.RecognizeAsync(service, image, new[] { Line("outside") }, new[] { OcrLanguage.English }, options, default);
        Assert.Equal(30, Assert.Single(proxy.Recognized)[0].Y);
    }

    [Fact]
    public async Task Unsupported_custom_service_fails_informatively()
    {
        using var image = new Image<Rgb24>(200, 100);
        using var service = new ServiceFakeOcrService();
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => HybridUncoveredRecognition.RecognizeAsync(service, image,
            Array.Empty<OcrLine>(), new[] { OcrLanguage.English }, SourceOptions, default));
        Assert.Contains("DetectRegionsAsync and RecognizeRegionsAsync", error.Message);
    }

    [Fact]
    public void Page_transform_options_are_rejected_instead_of_silently_ignored()
    {
        Assert.Throws<ArgumentException>(() => HybridUncoveredRecognition.Validate(null));
        Assert.Throws<ArgumentException>(() => HybridUncoveredRecognition.Validate(RecognitionOptions.Default));
        Assert.Throws<ArgumentException>(() => HybridUncoveredRecognition.Validate(SourceOptions with { UseDocUnwarp = true }));
        Assert.Throws<ArgumentException>(() => HybridUncoveredRecognition.Validate(SourceOptions with { Preprocessing = new() { Deskew = true, CorrectNonSquarePixels = false } }));
        Assert.Throws<ArgumentException>(() => new PdfOcrOptions { HybridRecognizeUncoveredOnly = true }.Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_pipeline_preserves_default_full_page_path_and_discloses_selective_mode(bool selective)
    {
        var service = DispatchProxy.Create<IPaddleOcrService, SelectiveProxy>();
        var proxy = (SelectiveProxy)(object)service;
        string path = Path.Combine(OcrDatasetBenchmarkTests.RepoRoot, "test", "Assets", "pdf", "invoice_778899.pdf");
        var result = await service.ExtractTextFromPdfAsync(path, OcrLanguage.English,
            options: selective ? SourceOptions : null,
            pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Hybrid, HybridRecognizeUncoveredOnly = selective });
        Assert.Equal(selective ? 0 : 1, proxy.FullPageCalls);
        Assert.Equal(selective ? 1 : 0, proxy.DetectionCalls);
        var page = Assert.Single(result.Pages);
        Assert.Equal(selective, page.NativeTextVisualVerificationSkipped);
        Assert.Contains("INVOICE NUMBER 778899", page.Ocr.FullText);
    }

    private static DetectedRegion Region(OcrLine line) => new() { BoundingPolygon = line.BoundingPolygon, BoundingBox = line.BoundingBox };

    public class SelectiveProxy : DispatchProxy
    {
        public IReadOnlyList<DetectedRegion> Detected { get; set; } = Array.Empty<DetectedRegion>();
        public IReadOnlyList<IReadOnlyList<OcrPoint>> Recognized { get; private set; } = Array.Empty<IReadOnlyList<OcrPoint>>();
        public int FullPageCalls { get; private set; }
        public int DetectionCalls { get; private set; }
        public int RegionCalls { get; private set; }
        public RecognitionOptions? DetectionOptions { get; private set; }
        public RecognitionOptions? RecognitionOptions { get; private set; }
        public IReadOnlyList<OcrLanguage>? Languages { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case nameof(IPaddleOcrService.ExtractTextFromImage): FullPageCalls++; return Task.FromResult(OcrResult.Empty);
                case nameof(IPaddleOcrService.DetectRegionsAsync):
                    DetectionCalls++;
                    DetectionOptions = (RecognitionOptions?)args![1];
                    return Task.FromResult(Detected);
                case nameof(IPaddleOcrService.RecognizeRegionsAsync):
                    RegionCalls++;
                    Recognized = ((IEnumerable<IReadOnlyList<OcrPoint>>)args![1]!).ToArray();
                    Languages = (IReadOnlyList<OcrLanguage>)args[2]!;
                    RecognitionOptions = (RecognitionOptions?)args[3];
                    return Task.FromResult(OcrResult.Empty);
                default: throw new NotSupportedException(targetMethod.Name);
            }
        }
    }
}
