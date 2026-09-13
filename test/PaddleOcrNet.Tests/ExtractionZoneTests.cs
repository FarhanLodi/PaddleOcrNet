using PaddleOcrNet.Extraction;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-free tests for <see cref="ZoneOcrExtensions"/> using a recording <see cref="IPaddleOcrService"/> fake.
/// </summary>
public class ExtractionZoneTests
{
    private sealed class RecordingOcrService : IPaddleOcrService
    {
        public List<RecognitionOptions?> ExtractCalls { get; } = new();

        public List<(IReadOnlyList<OcrPoint> Polygon, RecognitionOptions? Options, IReadOnlyList<OcrLanguage> Languages)> RegionCalls { get; } = new();

        public Task<OcrResult> ExtractTextFromImage(Image<Rgb24> image, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        {
            ExtractCalls.Add(options);
            var (x, y, _, _) = options!.Region!.Value.Resolve(image.Width, image.Height);
            return Task.FromResult(Result(
                Line("World", x + 1, y + 12, x + 30, y + 22, 0.7),
                Line("Hello", x + 1, y + 1, x + 30, y + 10, 0.9)));
        }

        public Task<OcrResult> RecognizeRegionsAsync(Image<Rgb24> image, IEnumerable<IReadOnlyList<OcrPoint>> regions, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        {
            var polygon = regions.Single();
            RegionCalls.Add((polygon, options, languages));
            return Task.FromResult(Result(Line("2026-03-12", polygon[0].X, polygon[0].Y, polygon[2].X, polygon[2].Y, 0.95)));
        }

        public Task<OcrResult> ExtractTextFromImage(string imagePath, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(Stream imageStream, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(byte[] imageBytes, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(ReadOnlyMemory<byte> imageBytes, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static OcrLine Line(string text, double x1, double y1, double x2, double y2, double confidence) => new()
    {
        Text = text,
        Confidence = confidence,
        BoundingBox = new OcrBoundingBox(x1, y1, x2, y2),
    };

    private static OcrResult Result(params OcrLine[] lines) => new()
    {
        FullText = string.Join('\n', lines.Select(l => l.Text)),
        Lines = lines,
        Languages = new[] { "en" },
    };

    private static Image<Rgb24> NewImage() => new(200, 100, new Rgb24(255, 255, 255));

    [Fact]
    public async Task Detect_and_single_line_zones_are_read_in_order()
    {
        var service = new RecordingOcrService();
        using var image = NewImage();
        var digits = RecognitionOptions.FromCharacters("0123456789-");
        var zones = new[]
        {
            new OcrZone("header", OcrRegion.Fraction(0, 0, 1, 0.5)),
            new OcrZone("date", OcrRegion.Pixels(10, 60, 100, 20), ZoneMode.SingleLine, digits),
        };

        var result = await service.RecognizeZonesAsync(image, zones, OcrLanguage.English);

        Assert.Equal(new[] { "header", "date" }, result.Zones.Keys);
        Assert.Equal(200, result.SourceWidth);
        Assert.Equal(100, result.SourceHeight);

        var header = result["header"];
        Assert.Equal("Hello\nWorld", header.Text);
        Assert.Equal(0.8, header.Confidence, 6);
        Assert.Equal(2, header.Lines.Count);
        var detectOptions = Assert.Single(service.ExtractCalls);
        Assert.Equal(OcrRegion.Fraction(0, 0, 1, 0.5), detectOptions!.Region);
        Assert.Null(detectOptions.Allowlist);

        var date = result["date"];
        Assert.Equal("2026-03-12", date.Text);
        Assert.Equal(0.95, date.Confidence, 6);
        var call = Assert.Single(service.RegionCalls);
        Assert.Equal(new OcrPoint[] { new(10, 60), new(110, 60), new(110, 80), new(10, 80) }, call.Polygon);
        Assert.Null(call.Options!.Region);
        Assert.Same(digits, call.Options.Allowlist);
        Assert.Equal(new[] { OcrLanguage.English }, call.Languages);
    }

    [Fact]
    public async Task Base_options_are_kept_and_zone_allowlist_falls_back_to_them()
    {
        var service = new RecordingOcrService();
        using var image = NewImage();
        var baseAllowlist = RecognitionOptions.FromCharacters("ABC");
        var options = new RecognitionOptions { DropScore = 0.3, Allowlist = baseAllowlist };

        await service.RecognizeZonesAsync(image, new[] { new OcrZone("a", OcrRegion.Pixels(0, 0, 50, 50)) }, new[] { OcrLanguage.French, OcrLanguage.German }, options);

        var used = Assert.Single(service.ExtractCalls);
        Assert.Equal(0.3, used!.DropScore);
        Assert.Same(baseAllowlist, used.Allowlist);
    }

    [Fact]
    public async Task Empty_zone_yields_empty_result_without_calling_the_service()
    {
        var service = new RecordingOcrService();
        using var image = NewImage();

        var result = await service.RecognizeZonesAsync(image, new[] { new OcrZone("nothing", OcrRegion.Pixels(500, 500, 10, 10)) });

        Assert.Equal(string.Empty, result["nothing"].Text);
        Assert.Empty(result["nothing"].Lines);
        Assert.Equal(0, result["nothing"].Confidence);
        Assert.Empty(service.ExtractCalls);
    }

    [Fact]
    public async Task Invalid_zones_are_rejected()
    {
        var service = new RecordingOcrService();
        using var image = NewImage();
        var region = OcrRegion.Pixels(0, 0, 10, 10);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecognizeZonesAsync(image, new[] { new OcrZone("a", region), new OcrZone("a", region) }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecognizeZonesAsync(image, new[] { new OcrZone(" ", region) }));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.RecognizeZonesAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png"), new[] { new OcrZone("a", region) }));
    }
}
