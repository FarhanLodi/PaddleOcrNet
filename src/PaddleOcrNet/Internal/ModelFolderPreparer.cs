using System.Diagnostics;
using PaddleOcrNet.Extraction;
using PaddleOcrNet.Services;

namespace PaddleOcrNet.Internal;

internal static class ModelFolderPreparer
{
    internal static async Task<ModelDownloadReport> PrepareAsync(
        IReadOnlyList<ModelAsset> assets,
        string destinationDirectory,
        string? sourceCachePath,
        ModelDownloadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        var stopwatch = Stopwatch.StartNew();
        foreach (var asset in assets)
        {
            if (string.IsNullOrEmpty(asset.Sha256))
                throw new ModelChecksumException($"Model '{asset.FileName}' has no known SHA256 checksum; a verified model folder cannot be prepared.");
        }

        var files = new List<ModelFileReport>(assets.Count);
        if (Directory.Exists(destination))
        {
            foreach (var asset in assets)
            {
                string path = Path.Combine(destination, asset.FileName);
                try
                {
                    if (!File.Exists(path)) throw new ModelDownloadException($"Model '{asset.FileName}' is missing.");
                    await ModelDownloadManager.VerifyChecksumAsync(asset, path, options, cancellationToken, deleteOnFailure: false).ConfigureAwait(false);
                }
                catch (PaddleOcrException ex)
                {
                    throw new ModelDownloadException(
                        $"Model folder '{destination}' is incomplete or failed verification. Use a new destination; existing folders are never overwritten.", ex);
                }
                files.Add(new ModelFileReport(asset.FileName, ModelFileStatus.Cached, new FileInfo(path).Length, path));
            }
            return Report();
        }

        string parent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The destination must not be a filesystem root.", nameof(destinationDirectory));
        string staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        string cache = ModelDownloadManager.ResolveCacheRoot(sourceCachePath);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var asset in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stagedPath = Path.Combine(staging, asset.FileName);
                string cachedPath = Path.Combine(cache, asset.FileName);
                bool copied = false;
                if (File.Exists(cachedPath))
                {
                    await CopyAsync(cachedPath, stagedPath, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await ModelDownloadManager.VerifyChecksumAsync(asset, stagedPath, options, cancellationToken).ConfigureAwait(false);
                        copied = true;
                    }
                    catch (ModelChecksumException) when (!options.Offline)
                    {
                        // Verification removes only the staged copy. Never mutate the caller's cache.
                    }
                }

                if (!copied)
                {
                    if (options.Offline)
                        throw new OfflineModelMissingException(
                            $"Model '{asset.FileName}' is not present in the source cache ('{cache}') and offline mode is enabled.");

                    // Network retries keep their existing policy; checksum corruption gets exactly one fresh retry.
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await ModelDownloadManager.EnsureModelAsync(asset, staging, options, logger: null, cancellationToken).ConfigureAwait(false);
                            break;
                        }
                        catch (ModelChecksumException) when (attempt == 0)
                        {
                        }
                    }
                }

                long size = new FileInfo(stagedPath).Length;
                if (copied) options.Progress?.Report(new ModelDownloadProgress(asset.FileName, size, size));
                files.Add(new ModelFileReport(asset.FileName, copied ? ModelFileStatus.Cached : ModelFileStatus.Downloaded,
                    size, Path.Combine(destination, asset.FileName)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            // A sibling keeps the rename on one filesystem. Readers see either no folder or the complete set.
            Directory.Move(staging, destination);
            return Report();
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        ModelDownloadReport Report() => new()
        {
            CacheDirectory = destination,
            Files = files,
            Duration = stopwatch.Elapsed,
        };
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }
}
