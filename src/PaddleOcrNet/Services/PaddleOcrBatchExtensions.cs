using PaddleOcrNet.Models;

namespace PaddleOcrNet.Services;

/// <summary>
/// Batch OCR for background workers: many image files through one <see cref="IPaddleOcrService"/> with
/// bounded concurrency, per-image error isolation, progress reporting and honest cancellation.
/// </summary>
public static class PaddleOcrBatchExtensions
{
    /// <summary>
    /// OCRs a sequence of image files across the given <see cref="OcrLanguage"/> values, yielding one
    /// <see cref="OcrBatchItem"/> per path. At most <see cref="OcrBatchOptions.MaxConcurrency"/> images are
    /// in flight; the path sequence is enumerated lazily, so it may be large or unbounded.
    /// <para>
    /// Cancelling <paramref name="cancellationToken"/> stops launching new images, cancels the in-flight
    /// ones and throws <see cref="OperationCanceledException"/> from the enumeration. Abandoning the
    /// enumeration early (<c>break</c>) cancels and drains the in-flight work the same way.
    /// </para>
    /// </summary>
    public static IAsyncEnumerable<OcrBatchItem> ExtractTextFromImagesAsync(
        this IPaddleOcrService service,
        IEnumerable<string> paths,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        OcrBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(languages);
        var runner = new OcrBatchRunner(service, paths, languages, options, batchOptions ?? OcrBatchOptions.Default);
        return runner.RunAsync(cancellationToken);
    }

    /// <summary>
    /// OCRs a sequence of image files in a single <see cref="OcrLanguage"/> (defaults to
    /// <see cref="OcrLanguage.Auto"/>). See the multi-language overload for the concurrency and
    /// cancellation semantics.
    /// </summary>
    public static IAsyncEnumerable<OcrBatchItem> ExtractTextFromImagesAsync(
        this IPaddleOcrService service,
        IEnumerable<string> paths,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        OcrBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default)
        => service.ExtractTextFromImagesAsync(paths, new[] { language }, options, batchOptions, cancellationToken);
}
