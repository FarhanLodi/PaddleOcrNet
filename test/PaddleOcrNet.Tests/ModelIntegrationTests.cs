using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-dependent end-to-end tests. These download ONNX models on first run, so they are marked
/// <c>[Trait("Category","Integration")]</c> and <b>skipped by default</b> in CI. Opt in by setting the
/// environment variable <c>PADDLEOCRNET_RUN_INTEGRATION=1</c> (e.g. on a nightly job with network access).
/// Run only the unit suite with: <c>dotnet test --filter "Category!=Integration"</c>.
/// </summary>
[Trait("Category", "Integration")]
public class ModelIntegrationTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";

    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    [SkippableFact]
    public async Task ExtractTextFromImage_recognizes_text_with_real_models()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run (downloads models).");

        await using var service = new PaddleOcrService();
        using var image = new Image<Rgb24>(320, 64, new Rgb24(255, 255, 255));

        var result = await service.ExtractTextFromImage(image, new[] { "en" });

        Assert.NotNull(result);
        Assert.NotNull(result.Lines);
    }

    [SkippableFact]
    public async Task DetectRegions_returns_polygons_with_real_models()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run (downloads models).");

        await using var service = new PaddleOcrService();
        using var image = new Image<Rgb24>(320, 64, new Rgb24(255, 255, 255));

        var regions = await service.DetectRegionsAsync(image);

        Assert.NotNull(regions);
    }
}
