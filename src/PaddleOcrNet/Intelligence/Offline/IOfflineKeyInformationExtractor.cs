using PaddleOcrNet.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PaddleOcrNet.Intelligence.Offline;

/// <summary>
/// Heuristic, offline Key-Information Extraction (KIE): given a document's OCR result (text lines + bounding
/// boxes) and a list of field labels (keys), it resolves each value purely from layout geometry — no network,
/// no LLM, no extra model, CPU-only. It complements the LLM-backed <see cref="IDocumentIntelligence"/>: use this
/// when a model endpoint is unavailable or undesirable and the document is a simple label/value form.
/// </summary>
public interface IOfflineKeyInformationExtractor
{
    /// <summary>
    /// Extracts each requested key from an already-computed <see cref="OcrResult"/> using layout heuristics
    /// (inline same-line, value-to-the-right, value-below). Performs no network or LLM call.
    /// </summary>
    /// <param name="ocr">The OCR result whose lines and bounding boxes drive the geometry.</param>
    /// <param name="keys">The field labels to look up, returned in request order.</param>
    /// <returns>
    /// A <see cref="KeyInformationResult"/> with one <see cref="ExtractedField"/> per requested key (value
    /// <c>null</c> when not found). <see cref="KeyInformationResult.RawJson"/>, <see cref="KeyInformationResult.Usage"/>
    /// and <see cref="KeyInformationResult.Model"/> are always <c>null</c> (they are LLM-specific).
    /// </returns>
    KeyInformationResult Extract(OcrResult ocr, IReadOnlyList<string> keys);

    /// <summary>
    /// OCRs the image file at <paramref name="imagePath"/> (using <see cref="OcrLanguage.Auto"/>), then runs
    /// <see cref="Extract"/> on the result.
    /// </summary>
    /// <param name="imagePath">Path to the document image to OCR.</param>
    /// <param name="keys">The field labels to look up, returned in request order.</param>
    /// <param name="cancellationToken">A token to cancel the OCR pass.</param>
    Task<KeyInformationResult> ExtractAsync(string imagePath, IReadOnlyList<string> keys, CancellationToken cancellationToken = default);

    /// <summary>
    /// OCRs the already-decoded <paramref name="image"/> (using <see cref="OcrLanguage.Auto"/>), then runs
    /// <see cref="Extract"/> on the result. The caller retains ownership of the image (it is not disposed).
    /// </summary>
    /// <param name="image">The decoded document image to OCR.</param>
    /// <param name="keys">The field labels to look up, returned in request order.</param>
    /// <param name="cancellationToken">A token to cancel the OCR pass.</param>
    Task<KeyInformationResult> ExtractAsync(Image<Rgb24> image, IReadOnlyList<string> keys, CancellationToken cancellationToken = default);
}
