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
}
