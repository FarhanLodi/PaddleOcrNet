using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;

namespace PaddleOcrNet.Tests;

/// <summary>
/// A model-free <see cref="IPaddleOcrService"/> for the frames and batch extension tests. Path calls run
/// <see cref="OnPath"/>; decoded-image calls run <see cref="OnImage"/>.
/// </summary>
internal sealed class ServiceFakeOcrService : IPaddleOcrService
{
    public Func<string, CancellationToken, Task<OcrResult>> OnPath { get; init; } =
        (path, _) => Task.FromResult(Result(path));

    public Func<Image<Rgb24>, OcrResult> OnImage { get; init; } =
        image => Result($"{image.Width}x{image.Height}");

    internal static OcrResult Result(string text, int width = 0, int height = 0) => new()
    {
        FullText = text,
        Lines = Array.Empty<OcrLine>(),
        Languages = new[] { "en" },
        SourceWidth = width,
        SourceHeight = height,
    };

    public Task<OcrResult> ExtractTextFromImage(string imagePath, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        => OnPath(imagePath, cancellationToken);

    public Task<OcrResult> ExtractTextFromImage(Stream imageStream, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OcrResult> ExtractTextFromImage(byte[] imageBytes, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OcrResult> ExtractTextFromImage(ReadOnlyMemory<byte> imageBytes, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OcrResult> ExtractTextFromImage(Image<Rgb24> image, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(OnImage(image));

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
