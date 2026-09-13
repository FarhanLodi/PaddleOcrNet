namespace PaddleOcrNet.Models;

/// <summary>
/// The OCR result for one frame of a multi-frame image — a page of a multi-page TIFF, or a frame of an
/// animated GIF, WebP or APNG.
/// </summary>
/// <param name="FrameIndex">Zero-based index of the frame within the image.</param>
/// <param name="Result">The OCR result for that frame, with coordinates in the frame's own pixel grid.</param>
public sealed record OcrFrameResult(int FrameIndex, OcrResult Result);
