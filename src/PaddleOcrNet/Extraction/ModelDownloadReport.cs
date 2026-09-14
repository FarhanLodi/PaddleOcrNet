namespace PaddleOcrNet.Extraction;

/// <summary>
/// The result of <see cref="PaddleOcrModels"/> pre-download: one entry per model file.
/// </summary>
public sealed record ModelDownloadReport
{
    /// <summary>The absolute cache directory the files were resolved against.</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>One entry per model file, in download order.</summary>
    public required IReadOnlyList<ModelFileReport> Files { get; init; }

    /// <summary>Total time spent.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Number of files that were already cached.</summary>
    public int CachedCount => Files.Count(f => f.Status == ModelFileStatus.Cached);

    /// <summary>Number of files that were downloaded.</summary>
    public int DownloadedCount => Files.Count(f => f.Status == ModelFileStatus.Downloaded);

    /// <summary>Number of files that could not be obtained.</summary>
    public int FailedCount => Files.Count(f => f.Status == ModelFileStatus.Failed);

    /// <summary>Total size in bytes of the files now in the cache.</summary>
    public long TotalBytes => Files.Sum(f => f.SizeBytes);

    /// <summary>True when every file is present in the cache.</summary>
    public bool IsComplete => FailedCount == 0;

    /// <summary>
    /// Throws when any file failed — convenient at the end of a Docker build step or deployment script.
    /// </summary>
    /// <exception cref="ModelDownloadException">One or more files could not be obtained; the first failure is the inner exception.</exception>
    public void EnsureSuccess()
    {
        var failed = Files.Where(f => f.Status == ModelFileStatus.Failed).ToList();
        if (failed.Count == 0) return;

        string message = $"{failed.Count} of {Files.Count} model file(s) could not be obtained into '{CacheDirectory}': "
            + string.Join(", ", failed.Select(f => f.FileName)) + ".";
        throw failed[0].Error is { } inner
            ? new ModelDownloadException(message, inner)
            : new ModelDownloadException(message);
    }
}
