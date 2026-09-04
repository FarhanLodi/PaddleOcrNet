using PaddleOcrNet.Internal;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="PaddleModelRegistry"/>. Pins the
/// language -> recognizer pack resolution: known languages resolve to a pack that serves them, lookup is
/// case-insensitive, and unknown codes return null. The exact pack catalogue is a downstream-filled STUB,
/// so these tests assert the resolution contract rather than a fixed pack inventory.
/// </summary>
public class PaddleModelRegistryTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("ch")]
    public void FindByLanguage_resolves_supported_language_to_a_pack_that_serves_it(string lang)
    {
        var pack = PaddleModelRegistry.FindByLanguage(lang);

        Assert.NotNull(pack);
        Assert.Contains(lang, pack!.Languages);
    }

    [Fact]
    public void FindByLanguage_english_resolves()
    {
        var pack = PaddleModelRegistry.FindByLanguage("en");
        Assert.NotNull(pack);
        Assert.Contains("en", pack!.Languages);
    }

    [Theory]
    [InlineData("EN")]
    [InlineData("En")]
    [InlineData("CH")]
    public void FindByLanguage_is_case_insensitive(string lang)
        => Assert.NotNull(PaddleModelRegistry.FindByLanguage(lang));

    [Theory]
    [InlineData("xx")]   // nonsense
    [InlineData("zzz")]  // nonsense
    public void FindByLanguage_returns_null_for_unsupported(string lang)
        => Assert.Null(PaddleModelRegistry.FindByLanguage(lang));

    [Fact]
    public void Every_pack_has_a_distinct_name_and_at_least_one_language()
    {
        var names = PaddleModelRegistry.All.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(PaddleModelRegistry.All, p => Assert.NotEmpty(p.Languages));
    }

    [Fact]
    public void Every_language_in_a_pack_resolves_back_to_a_pack()
    {
        foreach (var pack in PaddleModelRegistry.All)
        {
            foreach (var lang in pack.Languages)
            {
                Assert.NotNull(PaddleModelRegistry.FindByLanguage(lang));
            }
        }
    }

    [Fact]
    public void Detector_and_pack_assets_are_onnx_models_with_dictionaries()
    {
        Assert.EndsWith(".onnx", PaddleModelRegistry.Detector.FileName);
        foreach (var pack in PaddleModelRegistry.All)
        {
            Assert.EndsWith(".onnx", pack.Model.FileName);
            Assert.NotEmpty(pack.Dictionary.FileName);
        }
    }

    // ---- Python-parity language routing -----------------------------------------------------------

    [Fact]
    public void English_routes_to_the_dedicated_en_pack()
    {
        var pack = PaddleModelRegistry.FindByLanguage("en");

        Assert.NotNull(pack);
        Assert.Equal("en_PP-OCRv5_mobile", pack!.Name);
        Assert.Equal("en_PP-OCRv5_mobile_rec_infer.onnx", pack.Model.FileName);
        Assert.Equal("ppocrv5_en_dict.txt", pack.Dictionary.FileName);
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("japan")]
    [InlineData("ja_full")]
    public void Japanese_routes_to_the_default_v5_mobile_pack(string lang)
    {
        // Upstream ships no Japanese PP-OCRv5 recognizer; the default pack's ppocrv5_dict covers ja.
        var pack = PaddleModelRegistry.FindByLanguage(lang);

        Assert.NotNull(pack);
        Assert.Equal("PP-OCRv5_mobile", pack!.Name);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("be")]
    [InlineData("uk")]
    public void East_slavic_languages_route_to_the_eslav_pack(string lang)
    {
        // Python checks ESLAV_LANGS before the Cyrillic group.
        var pack = PaddleModelRegistry.FindByLanguage(lang);

        Assert.NotNull(pack);
        Assert.Equal("eslav_PP-OCRv5_mobile", pack!.Name);
    }

    [Theory]
    // New Latin-script codes.
    [InlineData("fi", "latin_PP-OCRv5_mobile")]
    [InlineData("eu", "latin_PP-OCRv5_mobile")]
    [InlineData("gl", "latin_PP-OCRv5_mobile")]
    [InlineData("lb", "latin_PP-OCRv5_mobile")]
    [InlineData("rm", "latin_PP-OCRv5_mobile")]
    [InlineData("ca", "latin_PP-OCRv5_mobile")]
    [InlineData("qu", "latin_PP-OCRv5_mobile")]
    [InlineData("french", "latin_PP-OCRv5_mobile")]
    [InlineData("german", "latin_PP-OCRv5_mobile")]
    // New Cyrillic-script codes.
    [InlineData("kk", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("ky", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("tg", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("mk", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("tt", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("cv", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("ba", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("mhr", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("mo", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("udm", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("kv", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("os", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("bua", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("xal", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("tyv", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("sah", "cyrillic_PP-OCRv5_mobile")]
    [InlineData("kaa", "cyrillic_PP-OCRv5_mobile")]
    // New Arabic-script codes.
    [InlineData("ps", "arabic_PP-OCRv5_mobile")]
    [InlineData("sd", "arabic_PP-OCRv5_mobile")]
    [InlineData("bal", "arabic_PP-OCRv5_mobile")]
    public void New_language_codes_resolve_to_their_script_pack(string lang, string packName)
    {
        var pack = PaddleModelRegistry.FindByLanguage(lang);

        Assert.NotNull(pack);
        Assert.Equal(packName, pack!.Name);
    }

    [Fact]
    public void Server_variant_assets_exist_with_verified_checksums()
    {
        // OcrModelVariant.Server switches the engine to these assets; they must be hosted & pinned.
        Assert.EndsWith(".onnx", PaddleModelRegistry.ServerDetector.FileName);
        Assert.False(string.IsNullOrEmpty(PaddleModelRegistry.ServerDetector.Sha256));

        Assert.EndsWith(".onnx", PaddleModelRegistry.ServerRecognizer.Model.FileName);
        Assert.False(string.IsNullOrEmpty(PaddleModelRegistry.ServerRecognizer.Model.Sha256));
        Assert.False(string.IsNullOrEmpty(PaddleModelRegistry.ServerRecognizer.Dictionary.Sha256));
    }

    [Fact]
    public void English_pack_assets_have_verified_checksums()
    {
        var pack = PaddleModelRegistry.FindByLanguage("en")!;
        Assert.False(string.IsNullOrEmpty(pack.Model.Sha256));
        Assert.False(string.IsNullOrEmpty(pack.Dictionary.Sha256));
    }
}
