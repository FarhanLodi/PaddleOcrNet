using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
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

        var result = await service.ExtractTextFromImage(image, OcrLanguage.English);

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

    [SkippableTheory]
    [InlineData("lang_en_typed.png", "en")]
    [InlineData("lang_en_handwritten.png", "en")]
    [InlineData("lang_zh_typed.png", "ch")]
    [InlineData("lang_zh_handwritten.png", "ch")]
    [InlineData("lang_ja_typed.png", "japan")]
    [InlineData("lang_ja_handwritten.png", "japan")]
    [InlineData("lang_ko_typed.png", "korean")]
    [InlineData("lang_ko_handwritten.png", "korean")]
    [InlineData("lang_cy_typed.png", "cyrillic")]
    [InlineData("lang_cy_handwritten.png", "cyrillic")]
    [InlineData("lang_ar_typed.png", "ar")]
    [InlineData("lang_ar_handwritten.png", "ar")]
    [InlineData("lang_hi_typed.png", "devanagari")]
    [InlineData("lang_hi_handwritten.png", "devanagari")]
    public async Task ExtractTextFromGeneratedImages_runs_successfully(string filename, string language)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        var assetsDir = Path.Combine(AppContext.BaseDirectory, "../../../test/Assets");
        if (!Directory.Exists(assetsDir))
        {
            // fallback
            assetsDir = Path.Combine(Directory.GetCurrentDirectory(), "test/Assets");
        }
        var imagePath = Path.Combine(assetsDir, filename);

        Assert.True(File.Exists(imagePath), $"Test image not found at: {imagePath}");

        await using var service = new PaddleOcrService();
        var result = await service.ExtractTextFromImage(imagePath, OcrLanguageExtensions.FromCode(language));

        Assert.NotNull(result);
        Assert.NotNull(result.Lines);
    }
}

