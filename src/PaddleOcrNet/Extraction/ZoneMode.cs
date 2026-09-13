namespace PaddleOcrNet.Extraction;

/// <summary>
/// How an <see cref="OcrZone"/> is read.
/// </summary>
public enum ZoneMode
{
    /// <summary>
    /// Run normal detection + recognition restricted to the zone (via
    /// <see cref="PaddleOcrNet.Models.RecognitionOptions.Region"/>). Suits zones that may hold several
    /// lines or words.
    /// </summary>
    Detect = 0,

    /// <summary>
    /// Skip detection and recognize the whole zone rectangle as a single text line (via
    /// <c>RecognizeRegionsAsync</c>). Suits tight single-line fields such as a date or an amount box,
    /// where detection might miss faint or short text. Tall zones (height ≥ 1.5 × width) are read as
    /// vertical text.
    /// </summary>
    SingleLine = 1,
}
