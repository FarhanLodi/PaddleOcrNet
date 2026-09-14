namespace PaddleOcrNet.Pdf;

/// <summary>
/// Where the text of a <see cref="PdfPageResult"/> came from.
/// </summary>
public enum PdfPageSource
{
    /// <summary>
    /// The page was rendered and recognized by the OCR engine.
    /// </summary>
    Ocr = 0,

    /// <summary>
    /// The page's embedded text layer was read directly (see <see cref="PdfOcrOptions.TextLayer"/>). Lines carry a
    /// confidence of 1.0 and coordinates in the same pixel space the OCR path reports.
    /// </summary>
    EmbeddedText = 1,
}
