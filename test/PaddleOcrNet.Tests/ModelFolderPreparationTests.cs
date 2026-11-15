using System.Net;
using System.Security.Cryptography;
using PaddleOcrNet.Extraction;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

public sealed class ModelFolderPreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "paddleocrnet-deploy-" + Guid.NewGuid().ToString("N"));
    private string Destination => Path.Combine(_root, "models");
    private string Cache => Path.Combine(_root, "cache");
    private static readonly byte[] ModelBytes = "synthetic model"u8.ToArray();
    private static readonly byte[] DictionaryBytes = "synthetic dictionary"u8.ToArray();
    private static readonly ModelAsset[] Assets =
    {
        Asset("model.onnx", ModelBytes),
        Asset("dictionary.txt", DictionaryBytes),
    };

    public ModelFolderPreparationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Fresh_prepare_downloads_verified_files_and_publishes_the_complete_folder()
    {
        using var handler = new FakeHandler(request =>
        {
            Assert.False(Directory.Exists(Destination));
            return BytesFor(request);
        });
        using var client = new HttpClient(handler);

        var report = await Prepare(Options(client));

        Assert.True(report.IsComplete);
        Assert.Equal(2, report.DownloadedCount);
        Assert.Equal(Path.GetFullPath(Destination), report.CacheDirectory);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(Path.Combine(Destination, Assets[0].FileName)));
        Assert.Equal(DictionaryBytes, await File.ReadAllBytesAsync(Path.Combine(Destination, Assets[1].FileName)));
        Assert.All(report.Files, file => Assert.Equal(Path.Combine(Destination, file.FileName), file.Path));
        AssertNoStagingFolder();
    }

    [Fact]
    public async Task Already_complete_folder_is_verified_without_downloading_or_rewriting()
    {
        await Seed(Destination);
        var path = Path.Combine(Destination, Assets[0].FileName);
        var timestamp = File.GetLastWriteTimeUtc(path);
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No download expected."));
        using var client = new HttpClient(handler);

        var report = await Prepare(Options(client));

        Assert.Equal(2, report.CachedCount);
        Assert.Equal(0, report.DownloadedCount);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        AssertNoStagingFolder();
    }

    [Fact]
    public async Task Verified_cache_is_copied_without_network_and_without_changing_the_cache()
    {
        await Seed(Cache);
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No download expected."));
        using var client = new HttpClient(handler);
        var options = Options(client);
        options.Offline = true;

        var report = await Prepare(options);

        Assert.Equal(2, report.CachedCount);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(Path.Combine(Destination, Assets[0].FileName)));
        Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(Path.Combine(Cache, Assets[0].FileName)));
        AssertNoStagingFolder();
    }

    [Fact]
    public async Task Corrupted_download_is_retried_once_then_fails_with_the_file_and_checksum()
    {
        using var handler = new FakeHandler(_ => "corrupted"u8.ToArray());
        using var client = new HttpClient(handler);
        var options = Options(client);
        options.MaxRetries = 5;

        var error = await Assert.ThrowsAsync<ModelChecksumException>(() => Prepare(options));

        Assert.Contains(Assets[0].FileName, error.Message);
        Assert.Contains("SHA256", error.Message);
        Assert.Equal(2, handler.Requests);
        Assert.False(Directory.Exists(Destination));
        AssertNoStagingFolder();
    }

    [Fact]
    public async Task Corrupted_download_can_succeed_on_the_single_checksum_retry()
    {
        int requests = 0;
        using var handler = new FakeHandler(request => ++requests == 1 ? "corrupted"u8.ToArray() : BytesFor(request));
        using var client = new HttpClient(handler);

        var report = await Prepare(Options(client));

        Assert.True(report.IsComplete);
        Assert.Equal(3, handler.Requests);
        Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(Path.Combine(Destination, Assets[0].FileName)));
        AssertNoStagingFolder();
    }

    [Fact]
    public async Task Corrupted_cache_is_not_trusted_and_is_not_deleted()
    {
        Directory.CreateDirectory(Cache);
        var cached = Path.Combine(Cache, Assets[0].FileName);
        await File.WriteAllTextAsync(cached, "corrupted");
        using var handler = new FakeHandler(BytesFor);
        using var client = new HttpClient(handler);

        var report = await Prepare(Options(client));

        Assert.Equal(2, report.DownloadedCount);
        Assert.Equal(2, handler.Requests);
        Assert.Equal("corrupted", await File.ReadAllTextAsync(cached));
        Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(Path.Combine(Destination, Assets[0].FileName)));
    }

    [Fact]
    public async Task Interrupted_publish_leaves_no_destination_or_temporary_folder()
    {
        await Seed(Cache);
        using var cancellation = new CancellationTokenSource();
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No download expected."));
        using var client = new HttpClient(handler);
        var options = Options(client);
        options.Offline = true;
        options.Progress = new InlineProgress(value =>
        {
            Assert.False(Directory.Exists(Destination));
            if (value.FileName == Assets[^1].FileName) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Prepare(options, cancellation.Token));

        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(Destination));
        AssertNoStagingFolder();
    }

    [Fact]
    public async Task Failed_rename_cleans_staging_without_replacing_an_existing_file()
    {
        using var handler = new FakeHandler(BytesFor);
        using var client = new HttpClient(handler);
        var options = Options(client);
        options.Progress = new InlineProgress(value =>
        {
            if (value.FileName == Assets[^1].FileName) File.WriteAllText(Destination, "keep");
        });

        await Assert.ThrowsAnyAsync<IOException>(() => Prepare(options));

        Assert.Equal("keep", await File.ReadAllTextAsync(Destination));
        Assert.False(Directory.Exists(Destination));
        AssertNoStagingFolder();
    }

    [Fact]
    public async Task Incomplete_existing_destination_is_never_overwritten()
    {
        Directory.CreateDirectory(Destination);
        await File.WriteAllTextAsync(Path.Combine(Destination, Assets[0].FileName), "corrupted");
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No download expected."));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => Prepare(Options(client)));

        Assert.Contains(Destination, error.Message);
        Assert.Contains("new destination", error.Message);
        Assert.Equal(0, handler.Requests);
        Assert.Equal("corrupted", await File.ReadAllTextAsync(Path.Combine(Destination, Assets[0].FileName)));
        AssertNoStagingFolder();
    }

    [Fact]
    public async Task Unknown_checksum_is_rejected_even_when_unverified_downloads_are_allowed()
    {
        using var handler = new FakeHandler(BytesFor);
        using var client = new HttpClient(handler);
        var options = Options(client);
        options.AllowUnverifiedModels = true;

        var error = await Assert.ThrowsAsync<ModelChecksumException>(() => ModelFolderPreparer.PrepareAsync(
            new[] { Assets[0] with { Sha256 = null } }, Destination, Cache, options, CancellationToken.None));

        Assert.Contains(Assets[0].FileName, error.Message);
        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(Destination));
        AssertNoStagingFolder();
    }

    [Fact]
    public async Task Public_prepare_uses_the_selected_set_and_explicit_source_cache_offline()
    {
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No download expected."));
        using var client = new HttpClient(handler);
        var options = new PaddleOcrServiceOptions { ModelCachePath = Cache, Download = Options(client) };
        options.Download.Offline = true;

        var error = await Assert.ThrowsAsync<OfflineModelMissingException>(() => PaddleOcrModels.PrepareAsync(
            Destination, new[] { OcrLanguage.English }, PaddleModelSet.Seal, options));

        Assert.Contains("PP-OCRv4_server_seal_det.onnx", error.Message);
        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(Destination));
        AssertNoStagingFolder();
    }

    [Fact]
    public async Task Offline_service_missing_model_reports_the_explicit_folder_without_downloading()
    {
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No download expected."));
        using var client = new HttpClient(handler);
        var options = new PaddleOcrServiceOptions
        {
            ModelCachePath = Destination,
            ExecutionProvider = OcrExecutionProvider.Cpu,
            Download = Options(client),
        };
        options.Download.Offline = true;
        await using var service = new PaddleOcrService(options);

        var error = await Assert.ThrowsAsync<OfflineModelMissingException>(() => service.WarmUp(new[] { OcrLanguage.English }));

        Assert.Contains(Destination, error.Message);
        Assert.Contains("PP-OCRv5_mobile_det.onnx", error.Message);
        Assert.Contains("offline", error.Message);
        Assert.Equal(0, handler.Requests);
    }

    private Task<ModelDownloadReport> Prepare(ModelDownloadOptions options, CancellationToken cancellationToken = default)
        => ModelFolderPreparer.PrepareAsync(Assets, Destination, Cache, options, cancellationToken);

    private static ModelAsset Asset(string fileName, byte[] bytes)
        => new(fileName, "https://models.invalid/" + fileName, Convert.ToHexString(SHA256.HashData(bytes)));

    private static byte[] BytesFor(HttpRequestMessage request)
        => request.RequestUri!.AbsolutePath.EndsWith(Assets[0].FileName, StringComparison.Ordinal) ? ModelBytes : DictionaryBytes;

    private static ModelDownloadOptions Options(HttpClient client)
        => new() { HttpClientFactory = () => client, MaxRetries = 0, RetryBaseDelay = TimeSpan.Zero };

    private static async Task Seed(string directory)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, Assets[0].FileName), ModelBytes);
        await File.WriteAllBytesAsync(Path.Combine(directory, Assets[1].FileName), DictionaryBytes);
    }

    private void AssertNoStagingFolder() => Assert.Empty(Directory.GetDirectories(_root, ".models.*.tmp"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class InlineProgress(Action<ModelDownloadProgress> report) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => report(value);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, byte[]> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response(request)) });
        }
    }
}
