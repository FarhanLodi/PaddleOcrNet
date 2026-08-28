using PaddleOcrNet.Models;

namespace PaddleOcrNet.Structure;

/// <summary>
/// Per-call configuration for <see cref="PaddleStructureEngine.AnalyzeAsync"/> /
/// <see cref="Services.IPaddleOcrService"/>'s document analysis. Controls document pre-processing
/// (orientation / unwarp), which sub-recognizers run (tables, formulas, seals), the layout model, and the
/// recognition language list passed through to the text recognizer.
/// </summary>
public sealed record StructureOptions
{
    /// <summary>
    /// Run whole-document orientation correction (0/90/180/270°) before layout detection. Default false.
    /// </summary>
    public bool UseDocOrientation { get; init; }

    /// <summary>
    /// Run document unwarping (UVDoc dewarp) before layout detection. Default false.
    /// </summary>
    public bool UseUnwarp { get; init; }

    /// <summary>
    /// Recognize the structure (HTML) of detected table regions. Default true.
    /// </summary>
    public bool RecognizeTables { get; init; } = true;

    /// <summary>
    /// Recognize the LaTeX of detected formula regions. Default true.
    /// </summary>
    public bool RecognizeFormulas { get; init; } = true;

    /// <summary>
    /// Recognize the text of detected seal regions. Default true.
    /// </summary>
    public bool RecognizeSeals { get; init; } = true;

    /// <summary>
    /// Which layout-detection model to use. Default <see cref="LayoutModel.RtDetrL"/> — the RT-DETR slot is
    /// served by the hosted PP-DocLayoutV3 model (the PicoDet S/M variants are not hosted yet).
    /// </summary>
    public LayoutModel LayoutModel { get; init; } = LayoutModel.RtDetrL;

    /// <summary>
    /// Confidence floor (0-1) for layout detections: a region whose model score is at or below this is
    /// discarded. Default 0.5 — the confidence floor both PP-DocLayoutV3 and PP-DocLayout-S/M ship in
    /// their own model configs. Lower it to keep faint
    /// regions the detector is unsure about (at the cost of false positives), raise it to keep only
    /// confident ones. Applies to whichever <see cref="LayoutModel"/> is selected.
    /// </summary>
    /// <remarks>
    /// Every kept region carries its own <see cref="LayoutRegion.Score"/>, so callers that prefer to filter
    /// themselves can set this low and post-filter per block type.
    /// </remarks>
    public float LayoutScoreThreshold { get; init; } = DefaultLayoutScoreThreshold;

    /// <summary>
    /// The default <see cref="LayoutScoreThreshold"/> (0.5), matching the shipped layout model configs.
    /// </summary>
    public const float DefaultLayoutScoreThreshold = 0.5f;

    /// <summary>
    /// Drop near-duplicate layout regions before returning them. The layout detectors emit a fixed top-k of
    /// candidates with no NMS, so the same area of the page is routinely proposed several times under
    /// different labels; this collapses each
    /// cluster to one region, drops sub-6px slivers, and removes <c>reference</c> markers whose text is
    /// carried by the neighbouring <c>reference_content</c> block. Barely visible at the default
    /// <see cref="LayoutScoreThreshold"/> — duplicates rarely score that high — and increasingly
    /// important as you lower it. Default true.
    /// </summary>
    public bool FilterOverlappingRegions { get; init; } = true;

    /// <summary>
    /// Additionally run non-maximum suppression over the layout regions, suppressing a lower-scoring region
    /// that overlaps a kept one by more than 0.6 IoU when both share a class, or 0.98 when they do not.
    /// Complements <see cref="FilterOverlappingRegions"/>, which measures overlap against the smaller box
    /// rather than the union. Default false — none of the shipped PP-DocLayout model configs enable it.
    /// </summary>
    public bool LayoutNms { get; init; }

    /// <summary>
    /// Grow every layout region about its own centre by this ratio before recognition: 1.1 adds 10% to the
    /// width and height, 1.0 changes nothing. Useful when
    /// tight boxes clip ascenders/descenders out of the crops handed to the table and formula recognizers.
    /// Expanded regions are re-clamped to the page. Default <c>null</c> (no expansion).
    /// </summary>
    public float? LayoutUnclipRatio { get; init; }

    /// <summary>
    /// How nested layout regions are resolved — keep the enclosing block, the inner blocks, or both.
    /// Default <see cref="LayoutMergeMode.None"/>: both are kept.
    /// </summary>
    public LayoutMergeMode LayoutMergeMode { get; init; } = LayoutMergeMode.None;

    /// <summary>
    /// Which source decides the reading order written into <see cref="StructureBlock.Order"/>. Default
    /// <see cref="LayoutReadingOrder.Auto"/>: the model's own predicted order when the layout model emits one
    /// (PP-DocLayoutV3 does), otherwise the geometric XY-cut orderer. Set
    /// <see cref="LayoutReadingOrder.XyCut"/> to always use XY-cut.
    /// </summary>
    public LayoutReadingOrder ReadingOrder { get; init; } = LayoutReadingOrder.Auto;

    /// <summary>
    /// Which table-structure model recovers <see cref="StructureBlockType.Table"/> regions. Default
    /// <see cref="TableRecognitionModel.SlanetPlus"/> (single end-to-end model). Set
    /// <see cref="TableRecognitionModel.SlaNeXt"/> to use the PP-StructureV3 v2 path (a wired/wireless
    /// classifier picks the matching SLANeXt model) — more accurate on clearly bordered/borderless tables;
    /// downloads three extra models on first use. Only consulted when <see cref="RecognizeTables"/> is true.
    /// </summary>
    public TableRecognitionModel TableModel { get; init; } = TableRecognitionModel.SlanetPlus;

    /// <summary>
    /// Recognition languages passed through to the text recognizer for text/caption/seal regions. Takes
    /// strongly-typed <see cref="OcrLanguage"/> values. Defaults to a single-element list of
    /// <see cref="OcrLanguage.ChineseSimplified"/> (code <c>"ch"</c>, which also covers English/Japanese).
    /// Use <see cref="OcrLanguage.Auto"/> for auto-detect.
    /// </summary>
    public IReadOnlyList<OcrLanguage> Languages { get; init; } = new[] { OcrLanguage.ChineseSimplified };

    /// <summary>
    /// Default structure options.
    /// </summary>
    public static StructureOptions Default { get; } = new();
}
