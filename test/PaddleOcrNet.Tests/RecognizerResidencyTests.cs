using PaddleOcrNet.Internal.Recognition;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

public sealed class RecognizerResidencyTests
{
    private sealed class Resource(Action? dispose = null) : IDisposable
    {
        internal int Disposals;
        public void Dispose() { Interlocked.Increment(ref Disposals); dispose?.Invoke(); }
    }

    [Fact]
    public async Task BusyEntryCannotBeEvictedAndWaitingAdmissionIsCancelable()
    {
        await using var cache = new LeasedResourceCache<Resource>(1);
        var first = new Resource();
        using var lease = await cache.AcquireAsync("first", () => Task.FromResult(first), default);
        using var canceled = new CancellationTokenSource();
        int loads = 0;
        var waiting = cache.AcquireAsync("second", () => { loads++; return Task.FromResult(new Resource()); }, canceled.Token);
        Assert.False(waiting.IsCompleted);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, loads);
        Assert.Equal(0, first.Disposals);
        lease.Dispose();
        using var second = await cache.AcquireAsync("second", () => Task.FromResult(new Resource()), default);
        Assert.Equal(1, first.Disposals);
    }

    [Fact]
    public async Task ConcurrentSameKeyLoadsAreSharedAndCanceledWaiterDoesNotPoisonLoad()
    {
        await using var cache = new LeasedResourceCache<Resource>(1);
        var loaded = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        int loads = 0;
        using var canceled = new CancellationTokenSource();
        Task<Resource> Load() { Interlocked.Increment(ref loads); return loaded.Task; }
        var first = cache.AcquireAsync("pack", Load, canceled.Token);
        var second = cache.AcquireAsync("PACK", Load, default);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var resource = new Resource();
        loaded.SetResult(resource);
        using var lease = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(resource, lease.Value);
        Assert.Equal(1, loads);
        Assert.Equal(0, resource.Disposals);
    }

    [Fact]
    public async Task FailedLoadIsRemovedForRetry()
    {
        await using var cache = new LeasedResourceCache<Resource>(1);
        await Assert.ThrowsAsync<IOException>(() => cache.AcquireAsync("a", () => Task.FromException<Resource>(new IOException()), default));
        using var lease = await cache.AcquireAsync("a", () => Task.FromResult(new Resource()), default);
        Assert.Equal(0, lease.Value.Disposals);
    }

    [Fact]
    public async Task IdleEntriesEvictInLeastRecentlyUsedOrder()
    {
        await using var cache = new LeasedResourceCache<Resource>(2);
        var a = new Resource(); var b = new Resource();
        (await cache.AcquireAsync("a", () => Task.FromResult(a), default)).Dispose();
        (await cache.AcquireAsync("b", () => Task.FromResult(b), default)).Dispose();
        (await cache.AcquireAsync("a", () => throw new Exception("Should reuse"), default)).Dispose();
        using var c = await cache.AcquireAsync("c", () => Task.FromResult(new Resource()), default);
        Assert.Equal(0, a.Disposals);
        Assert.Equal(1, b.Disposals);
    }

    [Fact]
    public async Task DisposalDrainsLeaseRejectsWaitersAndDisposesExactlyOnce()
    {
        var cache = new LeasedResourceCache<Resource>(1);
        var resource = new Resource();
        var lease = await cache.AcquireAsync("a", () => Task.FromResult(resource), default);
        var waiter = cache.AcquireAsync("b", () => Task.FromResult(new Resource()), default);
        var disposal = cache.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiter);
        Assert.Equal(0, resource.Disposals);
        lease.Dispose(); lease.Dispose();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await cache.DisposeAsync();
        Assert.Equal(1, resource.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.AcquireAsync("a", () => Task.FromResult(resource), default));
    }

    [Fact]
    public async Task DisposalWaitsForAbandonedLoadingEntryAndReleasesItsResult()
    {
        var cache = new LeasedResourceCache<Resource>(1);
        var completion = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var acquisition = cache.AcquireAsync("a", () => completion.Task, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        var disposal = cache.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        var resource = new Resource();
        completion.SetResult(resource);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public async Task UnlimitedDefaultRetainsAllIdleResources()
    {
        await using var cache = new LeasedResourceCache<Resource>(0);
        var resources = Enumerable.Range(0, 10).Select(_ => new Resource()).ToArray();
        for (int i = 0; i < resources.Length; i++)
        {
            var resource = resources[i];
            (await cache.AcquireAsync(i.ToString(), () => Task.FromResult(resource), default)).Dispose();
        }
        Assert.All(resources, r => Assert.Equal(0, r.Disposals));
    }

    [Fact]
    public async Task ConcurrentDistinctKeysNeverExceedResidentLimit()
    {
        var cache = new LeasedResourceCache<Resource>(2);
        int resident = 0, maximum = 0;
        object gate = new();
        var tasks = Enumerable.Range(0, 32).Select(async i =>
        {
            using var lease = await cache.AcquireAsync(i.ToString(), () =>
            {
                lock (gate) { resident++; maximum = Math.Max(maximum, resident); }
                return Task.FromResult(new Resource(() => { lock (gate) resident--; }));
            }, default);
            await Task.Yield();
            Assert.Equal(0, lease.Value.Disposals);
        }).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        await cache.DisposeAsync();
        Assert.InRange(maximum, 1, 2);
        Assert.Equal(0, resident);
    }

    [Fact]
    public void PublicLimitValidatesAndMapsToEngine()
    {
        Assert.Equal(0, new PaddleOcrServiceOptions().ToEngineOptions().MaxResidentRecognizers);
        Assert.Equal(1, new PaddleOcrServiceOptions { MaxResidentRecognizers = 1 }.ToEngineOptions().MaxResidentRecognizers);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaddleOcrServiceOptions { MaxResidentRecognizers = -1 }.ToEngineOptions());
    }
}
