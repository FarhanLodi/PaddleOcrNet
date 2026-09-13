namespace PaddleOcrNet.Models;

/// <summary>
/// Controls a batch OCR run (<c>ExtractTextFromImagesAsync</c>): how many images run at once, what happens
/// when one fails, progress reporting, and result ordering.
/// </summary>
public sealed record OcrBatchOptions
{
    private readonly int _maxConcurrency = 2;

    /// <summary>
    /// Maximum number of images processed concurrently. Default 2.
    /// <para>
    /// Higher is not automatically faster: ONNX Runtime already parallelizes each model run across the
    /// intra-op thread pool (by default one thread per physical core), so N concurrent images compete for
    /// the same cores and, beyond a small N, mostly add context switching and memory. For throughput on a
    /// dedicated CPU box, raise this together with lowering
    /// <c>PaddleOcrServiceOptions.IntraOpNumThreads</c> so that concurrency × intra-op threads ≈ physical
    /// cores. On a GPU the session serializes the device work, so 2–4 is usually the useful range.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxConcurrency
    {
        get => _maxConcurrency;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxConcurrency = value;
        }
    }

    /// <summary>
    /// When true (the default) a failing image is reported as an <see cref="OcrBatchItem"/> carrying its
    /// exception and the batch continues. When false the first failure cancels the remaining work and is
    /// rethrown from the enumeration.
    /// </summary>
    public bool ContinueOnError { get; init; } = true;

    /// <summary>
    /// Optional progress sink, notified each time an image finishes (from a worker thread, or through the
    /// captured synchronization context when it is a <see cref="Progress{T}"/>).
    /// </summary>
    public IProgress<OcrBatchProgress>? Progress { get; init; }

    /// <summary>
    /// When true (the default) items are yielded in input order; a finished image waits for every earlier
    /// one. When false items are yielded as soon as they finish.
    /// </summary>
    public bool PreserveOrder { get; init; } = true;

    /// <summary>
    /// The default batch options.
    /// </summary>
    public static OcrBatchOptions Default { get; } = new();
}
