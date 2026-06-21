using PaddleOcrNet.Models;

namespace PaddleOcrNet.Pdf;

/// <summary>
/// Aggregate OCR result for a whole PDF document.
/// </summary>
public sealed record PdfOcrResult
{
    /// <summary>
    /// Per-page results in document order.
    /// </summary>
    public required IReadOnlyList<PdfPageResult> Pages { get; init; }

    /// <summary>
    /// All pages' text concatenated, separated by blank lines.
    /// </summary>
    public string FullText => string.Join("\n\n", Pages.Select(p => p.Ocr.FullText));
}
