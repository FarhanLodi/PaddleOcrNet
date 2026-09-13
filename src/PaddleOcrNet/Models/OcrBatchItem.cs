namespace PaddleOcrNet.Models;

/// <summary>
/// The outcome of one image in a batch OCR run: either a <see cref="Result"/> or, when the image failed
/// and <see cref="OcrBatchOptions.ContinueOnError"/> is on, the <see cref="Exception"/> it failed with.
/// </summary>
/// <param name="Path">The image path, as supplied.</param>
/// <param name="Index">Zero-based position of the path in the input sequence.</param>
/// <param name="Result">The OCR result, or null when the image failed.</param>
/// <param name="Exception">The failure, or null when the image succeeded.</param>
/// <param name="Elapsed">Wall-clock time spent on this image.</param>
public sealed record OcrBatchItem(string Path, int Index, OcrResult? Result, Exception? Exception, TimeSpan Elapsed)
{
    /// <summary>
    /// True when the image was recognized successfully (<see cref="Result"/> is set).
    /// </summary>
    public bool Succeeded => Exception is null && Result is not null;
}
