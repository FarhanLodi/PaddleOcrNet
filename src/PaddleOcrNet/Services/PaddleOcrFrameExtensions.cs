using System.Diagnostics;
using System.Runtime.CompilerServices;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Diagnostics;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Services;

/// <summary>
/// Multi-frame OCR for <see cref="IPaddleOcrService"/>: every page of a multi-page TIFF (faxes, scanner
/// batches) and every frame of an animated GIF, WebP or APNG, yielded one <see cref="OcrFrameResult"/> at a
/// time. The single-image <c>ExtractTextFromImage</c> APIs only read the first frame.
/// <para>
/// The container is decoded once (EasyImageSharp has no incremental frame decoder), with every frame
/// checked against <see cref="PaddleOcrServiceOptions.MaxImagePixels"/> before it is allocated. Frames are
/// then detached, OCR'd and released one by one, so the decoded pixel memory shrinks as the enumeration
/// advances. Each frame gets the same load treatment as a single image (transparency flattening, EXIF
/// orientation, non-square pixel correction).
/// </para>
/// </summary>
public static class PaddleOcrFrameExtensions
{
    /// <summary>
    /// OCRs every frame of an image file (multi-page TIFF, animated GIF/WebP/APNG) across the given
    /// <see cref="OcrLanguage"/> values, yielding each frame's result as soon as it is ready.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static IAsyncEnumerable<OcrFrameResult> ExtractTextFromImageFramesAsync(
        this IPaddleOcrService service,
        string imagePath,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(languages);
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path must be provided.", nameof(imagePath));

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);

        return ExtractFramesCoreAsync(
            service, ct => GuardedImageLoader.ReadAllBytesAsync(fullPath, ct), languages, options, cancellationToken);
    }

    /// <summary>
    /// OCRs every frame of an image file in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    public static IAsyncEnumerable<OcrFrameResult> ExtractTextFromImageFramesAsync(
        this IPaddleOcrService service,
        string imagePath,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => service.ExtractTextFromImageFramesAsync(imagePath, new[] { language }, options, cancellationToken);

    /// <summary>
    /// OCRs every frame of an image read from a stream (format auto-detected) across the given
    /// <see cref="OcrLanguage"/> values. The stream is read to its end but not disposed.
    /// </summary>
    public static IAsyncEnumerable<OcrFrameResult> ExtractTextFromImageFramesAsync(
        this IPaddleOcrService service,
        Stream imageStream,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(imageStream);
        ArgumentNullException.ThrowIfNull(languages);
        return ExtractFramesCoreAsync(
            service, ct => GuardedImageLoader.ReadAllBytesAsync(imageStream, ct), languages, options, cancellationToken);
    }

    /// <summary>
    /// OCRs every frame of an image read from a stream in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    public static IAsyncEnumerable<OcrFrameResult> ExtractTextFromImageFramesAsync(
        this IPaddleOcrService service,
        Stream imageStream,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => service.ExtractTextFromImageFramesAsync(imageStream, new[] { language }, options, cancellationToken);

    private static async IAsyncEnumerable<OcrFrameResult> ExtractFramesCoreAsync(
        IPaddleOcrService service,
        Func<CancellationToken, Task<byte[]>> readBytes,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = PaddleOcrDiagnostics.ActivitySource.StartActivity("PaddleOcr.ExtractFrames", ActivityKind.Internal);
        var settings = service is PaddleOcrService concrete ? concrete.LoadSettings : ImageLoadSettings.Default;

        cancellationToken.ThrowIfCancellationRequested();
        Image decoded;
        {
            byte[] bytes = await readBytes(cancellationToken).ConfigureAwait(false);
            decoded = GuardedImageLoader.LoadAllFrames(bytes, settings);
        }

        using (decoded)
        {
            int total;
            switch (decoded)
            {
                case Image<Rgba32> rgba:
                    total = rgba.Frames.Count;
                    activity?.SetTag("paddleocr.frames", total);
                    for (int index = 0; index < total; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = await OcrNextFrameAsync(
                            service, rgba, TransparencyFlattener.Flatten, settings, languages, options, activity, cancellationToken).ConfigureAwait(false);
                        yield return new OcrFrameResult(index, result);
                    }
                    break;

                case Image<Rgb24> rgb:
                    total = rgb.Frames.Count;
                    activity?.SetTag("paddleocr.frames", total);
                    for (int index = 0; index < total; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = await OcrNextFrameAsync(
                            service, rgb, static img => img, settings, languages, options, activity, cancellationToken).ConfigureAwait(false);
                        yield return new OcrFrameResult(index, result);
                    }
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected decoded pixel format {decoded.GetType().Name}.");
            }
        }
    }

    /// <summary>
    /// Detaches the current root frame of <paramref name="decoded"/> (the image itself for the last frame),
    /// converts it to an opaque upright RGB image, OCRs it, and releases the frame's pixels.
    /// </summary>
    private static async Task<OcrResult> OcrNextFrameAsync<TPixel>(
        IPaddleOcrService service,
        Image<TPixel> decoded,
        Func<Image<TPixel>, Image<Rgb24>> toRgb,
        ImageLoadSettings settings,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options,
        Activity? parent,
        CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        bool isLast = decoded.Frames.Count == 1;
        Image<TPixel> frame = isLast ? decoded : decoded.Frames.ExportFrame(0);
        Image<Rgb24>? rgb = null;
        try
        {
            // A page's own EXIF (multi-page TIFFs carry orientation/resolution per page) wins over the
            // image-level profile, which describes the first page only.
            if (frame.Frames.RootFrame.Metadata.ExifProfile is { } pageExif)
            {
                frame.Metadata.ExifProfile = pageExif.DeepClone();
            }

            rgb = toRgb(frame);
            GuardedImageLoader.ApplyOrientation(rgb, settings);

            // Async iterators restore the caller's context between MoveNextAsync calls, so parent the
            // per-frame span explicitly (a change to Activity.Current here stays local to this method).
            if (parent is not null) Activity.Current = parent;
            return await service.ExtractTextFromImage(rgb, languages, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (rgb is not null && !ReferenceEquals(rgb, frame)) rgb.Dispose();
            if (!isLast) frame.Dispose();
        }
    }
}
