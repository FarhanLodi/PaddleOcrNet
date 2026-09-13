namespace PaddleOcrNet.Models;

/// <summary>
/// Progress of a batch OCR run, reported each time an image finishes.
/// </summary>
/// <param name="Completed">Images finished so far, successful or failed.</param>
/// <param name="Failed">Images that failed so far.</param>
/// <param name="Total">Total number of images when the input is a counted collection; otherwise null.</param>
/// <param name="LastPath">The path of the image that just finished.</param>
public sealed record OcrBatchProgress(int Completed, int Failed, int? Total, string LastPath);
