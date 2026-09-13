namespace PaddleOcrNet.Extraction;

/// <summary>
/// Groups of model files for <see cref="PaddleOcrModels.DownloadAsync(IEnumerable{PaddleOcrNet.Models.OcrLanguage}, PaddleModelSet, PaddleOcrNet.Services.ModelDownloadOptions?, IProgress{PaddleOcrNet.Services.ModelDownloadProgress}?, CancellationToken)"/>.
/// Combine with <c>|</c>. Files shared by several sets are fetched once.
/// </summary>
[Flags]
public enum PaddleModelSet
{
    /// <summary>No models.</summary>
    None = 0,

    /// <summary>
    /// Everything a default text-recognition call loads: the PP-OCRv5 DB text detector, the recognizer pack
    /// and character dictionary for each requested language, the text-line orientation classifier and the
    /// document orientation classifier.
    /// </summary>
    Ocr = 1 << 0,

    /// <summary>
    /// The orientation classifiers only (PP-LCNet text-line 0/180° and document 0/90/180/270°). Already
    /// included in <see cref="Ocr"/>.
    /// </summary>
    Orientation = 1 << 1,

    /// <summary>The UVDoc document unwarp (dewarp) model.</summary>
    Unwarp = 1 << 2,

    /// <summary>The default layout detector used by document-structure analysis: PP-DocLayoutV3 and its label file.</summary>
    Layout = 1 << 3,

    /// <summary>The lighter PicoDet layout detectors PP-DocLayout-S and PP-DocLayout-M with their label files.</summary>
    LayoutPicoDet = 1 << 4,

    /// <summary>The default table-structure model, SLANet_plus, plus the document orientation classifier used for table orientation classification.</summary>
    Table = 1 << 5,

    /// <summary>The SLANeXt table pipeline: the wired/wireless table classifier, SLANeXt_wired, SLANet_plus and the document orientation classifier.</summary>
    TableSlaNeXt = 1 << 6,

    /// <summary>The LaTeX-OCR formula recognizer (image resizer, encoder, decoder and tokenizer).</summary>
    Formula = 1 << 7,

    /// <summary>The PP-OCRv4 seal (curved text) detector. Seal text is read with the <see cref="Ocr"/> recognizers.</summary>
    Seal = 1 << 8,

    /// <summary>The higher-accuracy PP-OCRv5 server detector and server recognizer (with its dictionary).</summary>
    ServerModels = 1 << 9,

    /// <summary>What document-structure analysis loads with default options: <see cref="Ocr"/>, <see cref="Layout"/>, <see cref="Table"/>, <see cref="Formula"/> and <see cref="Seal"/>.</summary>
    Structure = Ocr | Layout | Table | Formula | Seal,

    /// <summary>Every hosted model set.</summary>
    All = Ocr | Orientation | Unwarp | Layout | LayoutPicoDet | Table | TableSlaNeXt | Formula | Seal | ServerModels,
}
