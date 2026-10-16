using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Internal.Recognition;
using PaddleOcrNet.Models;
using PaddleOcrNet.Structure.Seal;
using Xunit;

namespace PaddleOcrNet.Tests;

public sealed class SealLeaseCancellationTests
{
    [Fact]
    public async Task Canceled_seal_does_not_wait_for_an_unrelated_busy_pack_at_capacity_one()
    {
        await using var cache = new LeasedResourceCache<ITextRecognizer>(1);
        var busy = new FakeRecognizer();
        using var busyLease = await cache.AcquireAsync("busy", () => Task.FromResult<ITextRecognizer>(busy), default);
        int sealLoads = 0;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seal = new FakeRecognizer();
        var facade = new LeasedTextRecognizer(token =>
        {
            waiting.TrySetResult();
            return cache.AcquireAsync("seal", () => { Interlocked.Increment(ref sealLoads); return Task.FromResult<ITextRecognizer>(seal); }, token);
        });
        using var image = new Image<Rgb24>(20, 10);
        using var canceled = new CancellationTokenSource();
        var pending = Task.Run(() => SealRecognizer.RecognizeStrips(facade, new[] { image }, canceled.Token));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, sealLoads);
        Assert.Equal(0, busy.Disposals);

        busyLease.Dispose();
        var readings = SealRecognizer.RecognizeStrips(facade, new[] { image }, default);
        Assert.Equal("seal", Assert.Single(readings).Text);
        Assert.Equal(1, sealLoads);
        Assert.Equal(1, busy.Disposals);
    }

    [Fact]
    public async Task Seal_strips_preserve_legacy_batch_settings_and_forward_cancellation_into_recognition()
    {
        await using var cache = new LeasedResourceCache<ITextRecognizer>(1);
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeRecognizer { DuringRecognition = () => cancellation.Cancel() };
        var facade = new LeasedTextRecognizer(token => cache.AcquireAsync("seal", () => Task.FromResult<ITextRecognizer>(fake), token));
        using var image = new Image<Rgb24>(20, 10);
        Assert.ThrowsAny<OperationCanceledException>(() => SealRecognizer.RecognizeStrips(facade, new[] { image }, cancellation.Token));
        Assert.Equal(cancellation.Token, fake.Token);
        Assert.Equal(0, fake.Options!.BatchSize); // Keep the session constructor's batch size.
        Assert.Equal(1, fake.Options.MaxDegreeOfParallelism);
        Assert.Equal(0, fake.Options.SpaceRecoveryThreshold);
        Assert.Equal(1, fake.ConcurrentBatches);
        Assert.False(fake.IncludeCharacters);
        using var lease = await cache.AcquireAsync("seal", () => throw new InvalidOperationException("Canceled inference must not reload the healthy session."), default);
        Assert.Same(fake, lease.Value);
    }

    private sealed class FakeRecognizer : ITextRecognizer
    {
        public int Disposals { get; private set; }
        public RecognitionOptions? Options { get; private set; }
        public CancellationToken Token { get; private set; }
        public int ConcurrentBatches { get; private set; }
        public bool IncludeCharacters { get; private set; }
        public Action? DuringRecognition { get; init; }
        public IReadOnlyList<RecognizedText> RecognizeDetailed(IReadOnlyList<Image<Rgb24>> crops, RecognitionOptions options,
            int maxConcurrentBatches, bool includeCharacters, CancellationToken cancellationToken = default)
        {
            Options = options; Token = cancellationToken; ConcurrentBatches = maxConcurrentBatches; IncludeCharacters = includeCharacters;
            DuringRecognition?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return new[] { new RecognizedText("seal", .9f) };
        }
        public (string Text, float Confidence) Recognize(Image<Rgb24> crop) => throw new InvalidOperationException("Uncancellable overload used.");
        public IReadOnlyList<(string Text, float Confidence)> Recognize(IReadOnlyList<Image<Rgb24>> crops) => throw new InvalidOperationException("Uncancellable overload used.");
        public IReadOnlyList<(string Text, float Confidence)> Recognize(IReadOnlyList<Image<Rgb24>> crops, RecognitionOptions options) => throw new InvalidOperationException("Uncancellable overload used.");
        public void Dispose() => Disposals++;
    }
}
