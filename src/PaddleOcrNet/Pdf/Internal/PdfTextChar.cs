namespace PaddleOcrNet.Pdf.Internal;

/// <summary>
/// One character of a PDF page's embedded text layer, in rendered-page pixel coordinates (top-left origin).
/// Decoupled from Docnet's <c>Character</c> type so the line-building logic can be unit-tested without PDFium.
/// </summary>
/// <param name="Value">The UTF-16 code unit PDFium reported for the character.</param>
/// <param name="Left">Left edge in pixels.</param>
/// <param name="Top">Top edge in pixels.</param>
/// <param name="Right">Right edge in pixels.</param>
/// <param name="Bottom">Bottom edge in pixels.</param>
/// <param name="FontSizePx">The font size converted to pixels, or 0 when PDFium did not report one.</param>
internal readonly record struct PdfTextChar(char Value, double Left, double Top, double Right, double Bottom, double FontSizePx)
{
    /// <summary>
    /// The height used to scale word and column gap thresholds: the font size when known, otherwise the box height.
    /// </summary>
    public double EmHeight => FontSizePx > 0 ? FontSizePx : Math.Max(1.0, Bottom - Top);
}
