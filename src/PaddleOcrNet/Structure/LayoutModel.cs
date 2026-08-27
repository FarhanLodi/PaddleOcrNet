using PaddleOcrNet.Models;

namespace PaddleOcrNet.Structure;

/// <summary>
/// Selects which layout-detection model the structure pipeline uses. PP-DocLayout ships in S/M sizes and
/// the higher-accuracy RT-DETR ("plus-L") variant; all three are mapped onto a shared
/// <see cref="StructureBlockType"/> label set by the engine.
/// </summary>
public enum LayoutModel
{
    /// <summary>
    /// PP-DocLayout-S — the lightweight PicoDet-based layout detector (fastest, smallest).
    /// </summary>
    PicoDetS,

    /// <summary>
    /// PP-DocLayout-M — the medium PicoDet-based layout detector (balanced).
    /// </summary>
    PicoDetM,

    /// <summary>
    /// The RT-DETR-based layout detector, served by <c>PP-DocLayoutV3</c> — the RT-DETR layout network
    /// actually published as ONNX. This is the default and the validated path (25 classes, 800×800).
    /// PP-DocLayout_plus-L is deliberately not exposed: at 20 classes it is strictly dominated by
    /// PP-DocLayoutV3's 25 (which additionally splits formula into display/inline and adds header_image,
    /// footer_image, vertical_text and vision_footnote), so it would only ever be a downgrade.
    /// </summary>
    RtDetrL,
}
