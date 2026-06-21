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
        public Task<OcrResult> ExtractTextFromImage(SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default) => Record(languages);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
