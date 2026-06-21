using PaddleOcrNet.Models;

namespace PaddleOcrNet.Pdf;

/// <summary>
/// OCR result for a single rendered PDF page.
/// </summary>
public sealed record PdfPageResult
{
    /// <summary>
    /// 1-based page number.
    /// </summary>
    public required int PageNumber { get; init; }

    /// <summary>
    /// The recognized text and lines for this page.
    /// </summary>
    public required OcrResult Ocr { get; init; }

    /// <summary>
    /// Rendered page width in pixels (at the configured DPI).
    /// </summary>
    public int PixelWidth { get; init; }

    /// <summary>
    /// Rendered page height in pixels (at the configured DPI).
    /// </summary>
    public int PixelHeight { get; init; }
}
