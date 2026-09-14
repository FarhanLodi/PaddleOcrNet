namespace PaddleOcrNet.Extraction;

/// <summary>
/// The outcome for one model file in a <see cref="ModelDownloadReport"/>.
/// </summary>
/// <param name="FileName">The model file name (e.g. <c>PP-OCRv5_mobile_det.onnx</c>).</param>
/// <param name="Status">Whether the file was cached, downloaded, or failed.</param>
/// <param name="SizeBytes">The file size in bytes; 0 when it failed.</param>
/// <param name="Path">The absolute path of the file in the cache (where it would be, when it failed).</param>
/// <param name="Error">The failure, when <paramref name="Status"/> is <see cref="ModelFileStatus.Failed"/>.</param>
public sealed record ModelFileReport(
    string FileName,
    ModelFileStatus Status,
    long SizeBytes,
    string Path,
    Exception? Error = null);
