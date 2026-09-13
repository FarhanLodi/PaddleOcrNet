using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using PaddleOcrNet.Diagnostics;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Services;

/// <summary>
/// One batch run behind <see cref="PaddleOcrBatchExtensions.ExtractTextFromImagesAsync(IPaddleOcrService, IEnumerable{string}, IReadOnlyList{OcrLanguage}, RecognitionOptions?, OcrBatchOptions?, CancellationToken)"/>.
/// A pump enumerates the paths lazily and starts a worker per path behind a semaphore of
/// <see cref="OcrBatchOptions.MaxConcurrency"/>; workers publish finished items to a channel that the
/// consumer drains (re-ordering them when <see cref="OcrBatchOptions.PreserveOrder"/> is set).
/// </summary>
internal sealed class OcrBatchRunner
{
    private readonly IPaddleOcrService _service;
    private readonly IEnumerable<string> _paths;
    private readonly IReadOnlyList<OcrLanguage> _languages;
    private readonly RecognitionOptions? _options;
    private readonly OcrBatchOptions _batch;
    private readonly int? _total;
    private readonly Channel<OcrBatchItem> _channel =
        Channel.CreateUnbounded<OcrBatchItem>(new UnboundedChannelOptions { SingleReader = true });

    private int _completed;
    private int _failed;
    private Exception? _fatal;

    internal OcrBatchRunner(
        IPaddleOcrService service, IEnumerable<string> paths, IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options, OcrBatchOptions batch)
    {
        _service = service;
        _paths = paths;
        _languages = languages;
        _options = options;
        _batch = batch;
        _total = paths switch
        {
            IReadOnlyCollection<string> c => c.Count,
            ICollection<string> c => c.Count,
            _ => null,
        };
    }

    internal async IAsyncEnumerable<OcrBatchItem> RunAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = PaddleOcrDiagnostics.ActivitySource.StartActivity("PaddleOcr.ExtractBatch", ActivityKind.Internal);
        activity?.SetTag("paddleocr.batch.max_concurrency", _batch.MaxConcurrency);
        if (_total is { } total) activity?.SetTag("paddleocr.batch.total", total);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = PumpAsync(activity, cts);
        var pending = new Dictionary<int, OcrBatchItem>();
        int next = 0;
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_batch.PreserveOrder)
                {
                    yield return item;
                    continue;
                }

                pending[item.Index] = item;
                while (pending.Remove(next, out var ready))
                {
                    next++;
                    yield return ready;
                }
            }

            // The channel completes only after the pump has drained every worker; surface a fatal error
            // (ContinueOnError = false) or a cancellation that raced the last item.
            await pump.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            cts.Cancel();
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch
            {
                // Already surfaced above, or the consumer is abandoning / cancelling the enumeration.
            }

            activity?.SetTag("paddleocr.batch.completed", Volatile.Read(ref _completed));
            activity?.SetTag("paddleocr.batch.failed", Volatile.Read(ref _failed));
        }
    }

    private async Task PumpAsync(Activity? activity, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var running = new List<Task>();
        using var gate = new SemaphoreSlim(_batch.MaxConcurrency);
        try
        {
            int index = 0;
            foreach (var path in _paths)
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                running.Add(WorkerAsync(path, index++, gate, activity, cts));
                if (running.Count >= 4 * _batch.MaxConcurrency) running.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stop launching; in-flight workers observe the same token.
        }
        catch (Exception ex)
        {
            // The path sequence itself threw.
            Interlocked.CompareExchange(ref _fatal, ex, null);
            cts.Cancel();
        }
        finally
        {
            await Task.WhenAll(running).ConfigureAwait(false); // workers never fault
            _channel.Writer.TryComplete();
        }

        if (_fatal is { } fatal) ExceptionDispatchInfo.Capture(fatal).Throw();
    }

    private async Task WorkerAsync(string? path, int index, SemaphoreSlim gate, Activity? activity, CancellationTokenSource cts)
    {
        var token = cts.Token;
        long start = Stopwatch.GetTimestamp();
        try
        {
            if (activity is not null) Activity.Current = activity;

            OcrBatchItem item;
            try
            {
                var result = await _service.ExtractTextFromImage(path!, _languages, _options, token).ConfigureAwait(false);
                item = new OcrBatchItem(path ?? string.Empty, index, result, null, Stopwatch.GetElapsedTime(start));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return; // cancelled, not failed
            }
            catch (Exception ex) when (_batch.ContinueOnError)
            {
                Interlocked.Increment(ref _failed);
                item = new OcrBatchItem(path ?? string.Empty, index, null, ex, Stopwatch.GetElapsedTime(start));
            }

            int completed = Interlocked.Increment(ref _completed);
            _batch.Progress?.Report(new OcrBatchProgress(completed, Volatile.Read(ref _failed), _total, item.Path));
            _channel.Writer.TryWrite(item);
        }
        catch (Exception ex)
        {
            // ContinueOnError = false (or a progress callback threw): the first failure ends the batch.
            Interlocked.CompareExchange(ref _fatal, ex, null);
            cts.Cancel();
        }
        finally
        {
            gate.Release();
        }
    }
}
