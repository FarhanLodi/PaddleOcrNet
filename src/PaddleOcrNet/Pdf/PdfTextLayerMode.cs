namespace PaddleOcrNet.Pdf;

/// <summary>
/// Controls whether a PDF's embedded (born-digital) text layer is used instead of OCR-ing the rendered page.
/// </summary>
public enum PdfTextLayerMode
{
    /// <summary>
    /// Ignore any embedded text and OCR every page. This is the default and matches the behavior of earlier
    /// releases.
    /// </summary>
    Ignore = 0,

    /// <summary>
    /// Use the embedded text layer for every page whose text passes the quality gate (at least 20 characters,
    /// under 5% replacement, control or private-use characters, and at least 60% letters or digits). Pages that
    /// fail the gate, such as scans or documents with a broken ToUnicode map, are OCR'd.
    /// </summary>
    PreferEmbedded = 1,

    /// <summary>
    /// Like <see cref="PreferEmbedded"/>, but also OCRs pages whose embedded text lines cover less than 1% of the
    /// page area. That catches scanned pages carrying only a small digital stamp, header or footer line.
    /// </summary>
    Auto = 2,
}
