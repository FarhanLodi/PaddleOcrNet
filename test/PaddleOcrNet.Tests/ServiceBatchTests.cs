using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-free tests for <see cref="PaddleOcrBatchExtensions.ExtractTextFromImagesAsync(IPaddleOcrService, IEnumerable{string}, IReadOnlyList{OcrLanguage}, RecognitionOptions?, OcrBatchOptions?, CancellationToken)"/>
/// against a fake service.
/// </summary>
public class ServiceBatchTests
{
    private sealed class SyncProgress : IProgress<OcrBatchProgress>
    {
        public List<OcrBatchProgress> Reports { get; } = new();
        public void Report(OcrBatchProgress value) { lock (Reports) Reports.Add(value); }
    }

    private static string[] Paths(int n) => Enumerable.Range(0, n).Select(i => $"img{i}.png").ToArray();

    private static int IndexOf(string path) => int.Parse(path[3..^4]);

    private static async Task<List<OcrBatchItem>> Collect(IAsyncEnumerable<OcrBatchItem> items)
    {
        var list = new List<OcrBatchItem>();
        await foreach (var item in items) list.Add(item);
        return list;
    }

    [Fact]
    public async Task Preserves_input_order_by_default()
    {
        var service = new ServiceFakeOcrService
        {
            // Earlier items are slower, so completion order is reversed.
            OnPath = async (path, ct) =>
            {
                await Task.Delay((6 - IndexOf(path)) * 15, ct);
                return ServiceFakeOcrService.Result(path);
            },
        };

        var items = await Collect(service.ExtractTextFromImagesAsync(Paths(6), OcrLanguage.English,
            batchOptions: new OcrBatchOptions { MaxConcurrency = 3 }));

        Assert.Equal(Enumerable.Range(0, 6), items.Select(i => i.Index));
        Assert.All(items, i => Assert.True(i.Succeeded));
        Assert.All(items, i => Assert.Equal(i.Path, i.Result!.FullText));
    }

    [Fact]
    public async Task Unordered_mode_yields_in_completion_order()
    {
        var service = new ServiceFakeOcrService
        {
            OnPath = async (path, ct) =>
            {
                await Task.Delay(IndexOf(path) == 0 ? 300 : 10, ct);
                return ServiceFakeOcrService.Result(path);
            },
        };

        var items = await Collect(service.ExtractTextFromImagesAsync(Paths(3), OcrLanguage.English,
            batchOptions: new OcrBatchOptions { MaxConcurrency = 3, PreserveOrder = false }));

        Assert.Equal(3, items.Count);
        Assert.Equal(0, items[^1].Index);
    }

    [Fact]
    public async Task Concurrency_is_bounded()
    {
        int inFlight = 0, peak = 0;
        var service = new ServiceFakeOcrService
        {
            OnPath = async (path, ct) =>
            {
                int now = Interlocked.Increment(ref inFlight);
                int seen;
                while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                await Task.Delay(20, ct);
                Interlocked.Decrement(ref inFlight);
                return ServiceFakeOcrService.Result(path);
            },
        };

        var items = await Collect(service.ExtractTextFromImagesAsync(Paths(12), OcrLanguage.English,
            batchOptions: new OcrBatchOptions { MaxConcurrency = 2 }));

        Assert.Equal(12, items.Count);
        Assert.InRange(peak, 1, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OcrBatchOptions { MaxConcurrency = 0 });
    }

    [Fact]
    public async Task Failures_are_reported_per_item_and_progress_is_counted()
    {
        var service = new ServiceFakeOcrService
        {
            OnPath = (path, _) => IndexOf(path) == 2
                ? throw new InvalidOperationException("corrupt")
                : Task.FromResult(ServiceFakeOcrService.Result(path)),
        };
        var progress = new SyncProgress();

        var items = await Collect(service.ExtractTextFromImagesAsync(Paths(5).ToList(), new[] { OcrLanguage.English },
            batchOptions: new OcrBatchOptions { Progress = progress }));

        Assert.Equal(5, items.Count);
        var failed = Assert.Single(items, i => !i.Succeeded);
        Assert.Equal(2, failed.Index);
        Assert.IsType<InvalidOperationException>(failed.Exception);
        Assert.Null(failed.Result);

        Assert.Equal(5, progress.Reports.Count);
        Assert.Equal(5, progress.Reports.Max(r => r.Completed));
        Assert.Equal(1, progress.Reports.Max(r => r.Failed));
        Assert.All(progress.Reports, r => Assert.Equal(5, r.Total));
    }

    [Fact]
    public async Task Stop_on_error_rethrows_the_first_failure()
    {
        var service = new ServiceFakeOcrService
        {
            OnPath = async (path, ct) =>
            {
                if (IndexOf(path) == 1) throw new InvalidDataException("bad image");
                await Task.Delay(50, ct);
                return ServiceFakeOcrService.Result(path);
            },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(service.ExtractTextFromImagesAsync(
            Paths(20), OcrLanguage.English, batchOptions: new OcrBatchOptions { ContinueOnError = false })));
    }

    [Fact]
    public async Task Cancellation_stops_the_batch_and_throws()
    {
        using var cts = new CancellationTokenSource();
        int started = 0;
        var service = new ServiceFakeOcrService
        {
            OnPath = async (path, ct) =>
            {
                Interlocked.Increment(ref started);
                await Task.Delay(40, ct);
                return ServiceFakeOcrService.Result(path);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.ExtractTextFromImagesAsync(Paths(100), OcrLanguage.English,
                batchOptions: new OcrBatchOptions { MaxConcurrency = 2 }, cancellationToken: cts.Token))
            {
                cts.Cancel();
            }
        });

        Assert.True(started < 100, $"cancellation should stop launching work (started {started})");
    }

    [Fact]
    public async Task Breaking_out_early_cancels_in_flight_work()
    {
        var observedCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceFakeOcrService
        {
            OnPath = async (path, ct) =>
            {
                if (IndexOf(path) == 0) return ServiceFakeOcrService.Result(path);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    observedCancel.TrySetResult();
                    throw;
                }
                return ServiceFakeOcrService.Result(path);
            },
        };

        await foreach (var item in service.ExtractTextFromImagesAsync(Paths(4), OcrLanguage.English,
            batchOptions: new OcrBatchOptions { MaxConcurrency = 2 }))
        {
            Assert.Equal(0, item.Index);
            break;
        }

        Assert.True(observedCancel.Task.IsCompleted, "in-flight work is cancelled and drained before the enumeration returns");
    }
}
