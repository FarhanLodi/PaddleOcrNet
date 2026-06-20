using PaddleOcrNet.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PaddleOcrNet.Structure.Seal;

/// <summary>
/// Recognizes the text of a cropped seal / stamp region (curved/circular text). Implemented by
/// <see cref="SealRecognizer"/>, which runs a curved-text (seal) detector and then the shared text
/// recognizer on each detected line. Owns and disposes its seal-detector session (the injected text
/// recognizer is owned by the engine).
/// </summary>
internal interface ISealRecognizer : IDisposable
{
    /// <summary>
    /// Recognizes the text lines inside one seal crop.
    /// </summary>
    /// <param name="sealCrop">The cropped seal region (caller retains ownership).</param>
    /// <returns>The recognized seal text lines (in the crop's pixel coordinates); empty when none.</returns>
    IReadOnlyList<OcrLine> Recognize(Image<Rgb24> sealCrop);
}
