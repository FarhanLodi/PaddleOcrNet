using PaddleOcrNet.Models;

namespace PaddleOcrNet.Structure;

/// <summary>
/// Selects which layout-detection model the structure pipeline uses. PP-DocLayout ships in S/M sizes and
/// the higher-accuracy RT-DETR ("plus-L") variant; all three are mapped onto a shared
/// <see cref="StructureBlockType"/> label set by the engine.
/// </summary>
public enum LayoutModel
{
    /// <summary>PP-DocLayout-S — the lightweight PicoDet-based layout detector (fastest, smallest).</summary>
    PicoDetS,

    /// <summary>PP-DocLayout-M — the medium PicoDet-based layout detector (balanced).</summary>
    PicoDetM,

    /// <summary>PP-DocLayout_plus-L — the RT-DETR-based layout detector (highest accuracy, heaviest).</summary>
    RtDetrL,
}

/// <summary>
/// Per-call configuration for <see cref="PaddleStructureEngine.AnalyzeAsync"/> /
/// <see cref="Services.IPaddleOcrService"/>'s document analysis. Controls document pre-processing
/// (orientation / unwarp), which sub-recognizers run (tables, formulas, seals), the layout model, and the
/// recognition language list passed through to the text recognizer.
/// </summary>
public sealed record StructureOptions
{
    /// <summary>Run whole-document orientation correction (0/90/180/270°) before layout detection. Default false.</summary>
    public bool UseDocOrientation { get; init; }

    /// <summary>Run document unwarping (UVDoc dewarp) before layout detection. Default false.</summary>
    public bool UseUnwarp { get; init; }

    /// <summary>Recognize the structure (HTML) of detected table regions. Default true.</summary>
    public bool RecognizeTables { get; init; } = true;

    /// <summary>Recognize the LaTeX of detected formula regions. Default true.</summary>
    public bool RecognizeFormulas { get; init; } = true;

    /// <summary>Recognize the text of detected seal regions. Default true.</summary>
    public bool RecognizeSeals { get; init; } = true;

    /// <summary>
    /// Which layout-detection model to use. Default <see cref="LayoutModel.RtDetrL"/> — the RT-DETR slot is
    /// served by the hosted PP-DocLayoutV3 model (the PicoDet S/M variants are not hosted yet).
    /// </summary>
    public LayoutModel LayoutModel { get; init; } = LayoutModel.RtDetrL;

    /// <summary>
    /// Recognition languages passed through to the text recognizer for text/caption/seal regions. Takes
    /// strongly-typed <see cref="OcrLanguage"/> values. Defaults to a single-element list of
    /// <see cref="OcrLanguage.ChineseSimplified"/> (code <c>"ch"</c>, which also covers English/Japanese).
    /// Use <see cref="OcrLanguage.Auto"/> for auto-detect.
    /// </summary>
    public IReadOnlyList<OcrLanguage> Languages { get; init; } = new[] { OcrLanguage.ChineseSimplified };

    /// <summary>Default structure options.</summary>
    public static StructureOptions Default { get; } = new();
}
