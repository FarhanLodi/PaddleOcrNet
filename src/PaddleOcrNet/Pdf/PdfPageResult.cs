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

    /// <summary>
    /// The resolution this page was rendered at, and the pixel space of its coordinates. Equals
    /// <see cref="PdfOcrOptions.Dpi"/>, or the page's chosen resolution when that is <see cref="PdfOcrOptions.AutoDpi"/>.
    /// </summary>
    public int Dpi { get; init; }

    /// <summary>
    /// Whether the text came from OCR or from the PDF's embedded text layer. Default <see cref="PdfPageSource.Ocr"/>.
    /// </summary>
    public PdfPageSource Source { get; init; } = PdfPageSource.Ocr;
}
