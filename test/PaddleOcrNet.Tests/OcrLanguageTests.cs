using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Guards the <see cref="OcrLanguage"/> enum against drift from the recognizer registry: every value must
/// map to a code that <see cref="PaddleModelRegistry.FindByLanguage"/> resolves (or <c>"auto"</c>).
/// </summary>
public class OcrLanguageTests
{
    [Fact]
    public void Auto_maps_to_the_auto_detect_code()
        => Assert.Equal("auto", OcrLanguage.Auto.ToCode());

    [Fact]
    public void Every_enum_value_maps_to_a_resolvable_pack_or_auto()
    {
        foreach (OcrLanguage lang in Enum.GetValues<OcrLanguage>())
        {
            string code = lang.ToCode();
            Assert.False(string.IsNullOrWhiteSpace(code), $"{lang} mapped to an empty code");

            if (lang == OcrLanguage.Auto)
            {
                Assert.Equal("auto", code);
                continue;
            }

            Assert.True(
                PaddleModelRegistry.FindByLanguage(code) is not null,
                $"{lang} -> '{code}' did not resolve to a recognizer pack in the registry");
        }
    }

    [Fact]
    public void ToCodes_preserves_order_and_values()
    {
        string[] codes = new[] { OcrLanguage.English, OcrLanguage.French, OcrLanguage.Auto }.ToCodes();
        Assert.Equal(new[] { "en", "fr", "auto" }, codes);
    }

    [Fact]
    public async Task Enum_overloads_convert_and_forward_codes()
    {
        var fake = new RecordingOcrService();
        IPaddleOcrService svc = fake;   // overloads are default interface methods

        await svc.ExtractTextFromImage("x.png", OcrLanguage.French);
        Assert.Equal(new[] { "fr" }, fake.LastLanguages);

        await svc.ExtractTextFromImage("x.png", new[] { OcrLanguage.English, OcrLanguage.German });
        Assert.Equal(new[] { "en", "de" }, fake.LastLanguages);

        await svc.ExtractTextFromImage("x.png", OcrLanguage.Auto);
        Assert.Equal(new[] { "auto" }, fake.LastLanguages);
    }

    /// <summary>Records the language codes passed to the enum-based ExtractTextFromImage core methods (the
    /// single-language default interface overloads delegate to these), so assertions can read them back as
    /// codes. Everything else uses the interface's default (throwing) members.</summary>
    private sealed class RecordingOcrService : PaddleOcrNet.Services.IPaddleOcrService
    {
        public string[]? LastLanguages { get; private set; }

        private Task<OcrResult> Record(IReadOnlyList<OcrLanguage> languages)
        {
            LastLanguages = languages.ToCodes();
            return Task.FromResult(new OcrResult
            {
                FullText = string.Empty,
                Lines = Array.Empty<OcrLine>(),
                Languages = Array.Empty<string>()
            });
        }

        public Task<OcrResult> ExtractTextFromImage(string imagePath, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default) => Record(languages);
        public Task<OcrResult> ExtractTextFromImage(Stream imageStream, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default) => Record(languages);
        public Task<OcrResult> ExtractTextFromImage(byte[] imageBytes, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default) => Record(languages);
        public Task<OcrResult> ExtractTextFromImage(ReadOnlyMemory<byte> imageBytes, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default) => Record(languages);
        public Task<OcrResult> ExtractTextFromImage(EasyImageSharp.Image<EasyImageSharp.PixelFormats.Rgb24> image, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default) => Record(languages);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Several packs are named after PaddleOCR's pack file rather than the language ("korean", "japan",
    /// "ch"), which is an internal convention callers should not have to know: code read out of config
    /// or a CLI is far more likely to be the ISO tag. Those tags parse too — without shadowing any
    /// canonical code (notably "cy", which is Welsh, not Cyrillic).
    /// </summary>
    [Theory]
    [InlineData("ko", OcrLanguage.Korean)]
    [InlineData("ja", OcrLanguage.Japanese)]
    [InlineData("zh", OcrLanguage.ChineseSimplified)]
    [InlineData("zh-Hans", OcrLanguage.ChineseSimplified)]
    [InlineData("zh-Hant", OcrLanguage.ChineseTraditional)]
    [InlineData("cht", OcrLanguage.ChineseTraditional)]
    [InlineData("th", OcrLanguage.Thai)]
    [InlineData("el", OcrLanguage.Greek)]
    [InlineData("ta", OcrLanguage.Tamil)]
    [InlineData("te", OcrLanguage.Telugu)]
    public void Iso_tags_parse_for_pack_named_languages(string code, OcrLanguage expected)
    {
        Assert.Equal(expected, OcrLanguageExtensions.FromCode(code));
    }

    [Fact]
    public void Aliases_never_shadow_a_canonical_code()
    {
        // "cy" is Welsh's own canonical code; an alias must not steal it for Cyrillic.
        Assert.Equal(OcrLanguage.Welsh, OcrLanguageExtensions.FromCode("cy"));

        // Every canonical code still round-trips to its own language.
        foreach (var language in Enum.GetValues<OcrLanguage>())
            Assert.Equal(language, OcrLanguageExtensions.FromCode(language.ToCode()));
    }
}
