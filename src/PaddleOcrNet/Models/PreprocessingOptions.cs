namespace PaddleOcrNet.Models;

/// <summary>
/// Image clean-up applied before OCR. The clean-up steps (denoise, deskew, binarize, orientation) are off
/// by default; <see cref="CorrectNonSquarePixels"/> is on because it only affects images that declare
/// non-square pixels and reports coordinates in the original pixel grid. Deskew/denoise/binarize run on a
/// working copy; <see cref="DetectOrientation"/> is handled at the service level.
/// </summary>
public sealed record PreprocessingOptions
{
    /// <summary>
    /// Apply a light Gaussian denoise before detection. Default false.
    /// </summary>
    public bool Denoise { get; init; }

    /// <summary>
    /// Estimate a small skew angle (Hough transform on background-normalized text baselines, within ±15°)
    /// and straighten the page before detection. Boxes are mapped back onto the original, unrotated image.
    /// Default false.
    /// </summary>
    public bool Deskew { get; init; }

    /// <summary>
    /// Adaptive-threshold binarization (helpful for low-contrast scans). Default false.
    /// </summary>
    public bool Binarize { get; init; }

    /// <summary>
    /// Detect and correct the page orientation (0/90/180/270°). Uses the PP-LCNet document-orientation
    /// classifier in a single pass; only when that model is unavailable (e.g. offline with an empty cache)
    /// does it fall back to running OCR at all four rotations and keeping the strongest result. Boxes are
    /// reported in the original image's orientation. Default false. PaddleOCR's
    /// <c>use_textline_orientation</c> covers per-line 180° flips separately via the angle classifier.
    /// </summary>
    public bool DetectOrientation { get; init; }

    /// <summary>
    /// Resample images whose horizontal and vertical resolution differ by more than 15% (e.g. 204×98 DPI
    /// fax TIFFs) to square pixels before OCR, then map every coordinate back to the original pixel grid.
    /// Images without resolution metadata, or with square pixels, are untouched. Default true.
    /// </summary>
    public bool CorrectNonSquarePixels { get; init; } = true;

    /// <summary>
    /// No clean-up steps (the default). Non-square pixel correction stays on; see
    /// <see cref="CorrectNonSquarePixels"/>.
    /// </summary>
    public static PreprocessingOptions None { get; } = new();
}
