using PaddleOcrNet.Extraction;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-dependent end-to-end test for the extraction features: pre-downloads the English OCR models, then
/// reads zones from a bundled asset with real models. Skipped unless <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public class ExtractionIntegrationTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";

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

    [SkippableFact]
    public async Task Model_predownload_then_zone_ocr_with_real_models()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run (downloads models).");

        var languages = new[] { OcrLanguage.English };
        var report = await PaddleOcrModels.DownloadAsync(languages);
        report.EnsureSuccess();
        Assert.All(report.Files, f => Assert.True(File.Exists(f.Path), f.Path));

        // Everything is cached now, so an offline service must be able to run.
        var serviceOptions = new PaddleOcrServiceOptions { Download = { Offline = true } };
        await using var service = new PaddleOcrService(serviceOptions);

        string imagePath = Path.Combine(RepoRoot, "test", "Assets", "textline.png");
        var zones = new[]
        {
            new OcrZone("page", OcrRegion.Fraction(0, 0, 1, 1)),
            new OcrZone("line", OcrRegion.Fraction(0, 0, 1, 1), ZoneMode.SingleLine),
        };

        var result = await service.RecognizeZonesAsync(imagePath, zones, OcrLanguage.English);

        Assert.Equal(new[] { "page", "line" }, result.Zones.Keys);
        Assert.False(string.IsNullOrWhiteSpace(result["page"].Text));
        Assert.False(string.IsNullOrWhiteSpace(result["line"].Text));
        Assert.All(result["page"].Lines, l =>
        {
            Assert.InRange(l.BoundingBox.MinX, -1, result.SourceWidth + 1);
            Assert.InRange(l.BoundingBox.MaxY, -1, result.SourceHeight + 1);
        });
    }
}
