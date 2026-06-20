using PaddleOcrNet.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PaddleOcrNet.Structure.Table;

/// <summary>
/// Recovers the structure of a cropped table region as HTML, aligning the supplied OCR text lines into the
/// predicted cells. Implemented by <see cref="SlanetTableRecognizer"/> (SLANet / SLANeXt structure model).
/// Owns and disposes its ONNX session.
/// </summary>
internal interface ITableRecognizer : IDisposable
{
    /// <summary>
    /// Recognizes the structure of one table crop.
    /// </summary>
    /// <param name="tableCrop">The cropped table region (caller retains ownership).</param>
    /// <param name="ocrLines">
    /// OCR lines previously recognized inside the table crop (in the crop's pixel coordinates), to be
    /// distributed into the predicted cells.
    /// </param>
    /// <returns>The recovered table HTML and the predicted per-cell bounds.</returns>
    TableResult Recognize(Image<Rgb24> tableCrop, IReadOnlyList<OcrLine> ocrLines);
}
