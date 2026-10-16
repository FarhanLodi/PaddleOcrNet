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

    /// <summary>Hybrid selection evidence, in the same order as <see cref="Ocr"/> lines.</summary>
    public IReadOnlyList<PdfLineEvidence> LineEvidence { get; init; } = Array.Empty<PdfLineEvidence>();

    /// <summary>Canonical coordinates: rendered page pixels, origin at top left, X right and Y down.
    /// Plain OCR with an unavailable nonlinear source mapping instead declares dewarped-page-pixels.
    /// PDFium applies PDF page rotation and crop geometry before extraction; multiply source pixels by 72/Dpi
    /// to obtain points on that rendered page, not the original PDF user-space coordinates.</summary>
    public string CoordinateFrame => Ocr.Coordinates is { SourceMappingAvailable: false }
        ? "dewarped-page-pixels" : "rendered-page-pixels";

    /// <summary>Union area of valid native line rectangles divided by rendered page area; populated in Hybrid mode.</summary>
    public double NativeCoverageRatio { get; init; }

    /// <summary>
    /// True when uncovered-only Hybrid scheduling selected native text without a full-page visual recognition
    /// pass. Native text may be wrong despite valid Unicode and geometry. False is not an accuracy guarantee.
    /// </summary>
    public bool NativeTextVisualVerificationSkipped { get; init; }
}
