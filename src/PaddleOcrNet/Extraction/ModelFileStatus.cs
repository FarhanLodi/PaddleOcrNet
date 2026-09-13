namespace PaddleOcrNet.Extraction;

/// <summary>
/// Outcome for one file in a <see cref="ModelDownloadReport"/>.
/// </summary>
public enum ModelFileStatus
{
    /// <summary>The file was already in the cache; nothing was downloaded.</summary>
    Cached,

    /// <summary>The file was downloaded (and checksum-verified) into the cache.</summary>
    Downloaded,

    /// <summary>The file could not be obtained; see <see cref="ModelFileReport.Error"/>.</summary>
    Failed,
}
