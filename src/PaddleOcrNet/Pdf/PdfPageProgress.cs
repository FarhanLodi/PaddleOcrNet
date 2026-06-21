using System.Globalization;

namespace PaddleOcrNet.Pdf;

/// <summary>
/// Progress for PDF processing, reported per page via <see cref="PdfOcrOptions.Progress"/>.
/// </summary>
/// <param name="PageNumber">1-based <i>original</i> PDF page number being processed.</param>
/// <param name="PageCount">Total pages in the document.</param>
public readonly record struct PdfPageProgress(int PageNumber, int PageCount)
{
    /// <summary>
    /// Completion fraction (0–1).
    /// </summary>
    public double Fraction => PageCount > 0 ? (double)PageNumber / PageCount : 0;
}
