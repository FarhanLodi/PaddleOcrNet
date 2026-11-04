using System;
using System.Collections.Generic;
using PaddleOcrNet.Services;

namespace PaddleOcrNet.Models;

/// <summary>
/// Represents the result of an OCR operation.
/// </summary>
public sealed record OcrResult
{
    /// <summary>Applied geometry and source-mapping availability; null for results from older/custom producers.</summary>
    public OcrCoordinateProvenance? Coordinates { get; init; }

    /// <summary>
    /// Gets the concatenated text extracted from the image.
    /// </summary>
    public required string FullText { get; init; }

    /// <summary>
    /// Gets the collection of detailed line results.
    /// </summary>
    public required IReadOnlyList<OcrLine> Lines { get; init; }

    /// <summary>
    /// Gets the languages that were used during recognition.
    /// </summary>
    public required IReadOnlyList<string> Languages { get; init; }

    /// <summary>
    /// Gets the language code(s) inferred by automatic language detection, ordered by how many text crops
    /// each won (most first). Populated only when auto-detection ran (the request set
    /// <see cref="RecognitionOptions.AutoDetectLanguage"/> or passed the language code <c>"auto"</c>);
    /// otherwise empty.
    /// </summary>
    public IReadOnlyList<string> DetectedLanguages { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets the page orientation detected by the document-orientation classifier (or the brute-force
    /// orientation pass): the clockwise degrees (0, 90, 180 or 270) by which the input page was found
    /// rotated away from upright. 0 when the page was upright or orientation detection did not run
    /// (<see cref="RecognitionOptions.UseDocOrientation"/> off, or the classifier model unavailable).
    /// The pipeline internally rotated the page by <c>(360 − DetectedOrientation) % 360</c>° clockwise
    /// before reading it; <see cref="CoordinateFrame"/> identifies the returned geometry's orientation.
    /// </summary>
    public int DetectedOrientation { get; init; }

    /// <summary>Orientation frame selected for line and word geometry. Defaults to Original.</summary>
    public OcrResultCoordinateFrame CoordinateFrame { get; init; } = OcrResultCoordinateFrame.Original;

    /// <summary>Clockwise document rotation actually applied to upright the input (0, 90, 180 or 270).</summary>
    public int AppliedRotation { get; init; }

    /// <summary>Decoded input width before document rotation, or 0 when unknown.</summary>
    public int OriginalWidth { get; init; }

    /// <summary>Decoded input height before document rotation, or 0 when unknown.</summary>
    public int OriginalHeight { get; init; }

    /// <summary>Width of the decoded input rotated by AppliedRotation, or 0 when unknown.</summary>
    public int NormalizedWidth { get; init; }

    /// <summary>Height of the decoded input rotated by AppliedRotation, or 0 when unknown.</summary>
    public int NormalizedHeight { get; init; }

    /// <summary>
    /// Gets the clockwise rotation (degrees) that <see cref="PreprocessingOptions.Deskew"/> applied to
    /// straighten the page — the page's own skew was the negation of this. 0 when deskew is off or the skew
    /// was negligible. Deskew's enlarged canvas is mapped back before the selected
    /// <see cref="CoordinateFrame"/> is applied, so this angle is informational.
    /// </summary>
    public float DeskewAngle { get; init; }

    /// <summary>
    /// Gets the duration of the OCR operation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets a value indicating whether GPU acceleration was actually used for this operation —
    /// equivalent to <see cref="ExecutionProvider"/> being a non-CPU provider. Reflects the provider
    /// live at result-build time, so an accelerator that failed to attach and fell back to CPU reports
    /// <c>false</c> here.
    /// </summary>
    public bool UsedGpu { get; init; }

    /// <summary>
    /// Gets the execution provider the ONNX sessions were actually running on when this result was
    /// produced. <see cref="OcrExecutionProvider.Cpu"/> when an accelerator was requested but failed to
    /// attach (see <see cref="Services.PaddleOcrService.GpuAccelerationHint"/> for why).
    /// </summary>
    public OcrExecutionProvider ExecutionProvider { get; init; } = OcrExecutionProvider.Cpu;

    /// <summary>
    /// Gets the width (px) of the selected result coordinate frame, or 0 if unknown.
    /// Used by exporters (hOCR/ALTO) and for normalizing bounding boxes.
    /// </summary>
    public int SourceWidth { get; init; }

    /// <summary>
    /// Gets the height (px) of the selected result coordinate frame, or 0 if unknown.
    /// </summary>
    public int SourceHeight { get; init; }

    /// <summary>
    /// Creates an empty result instance.
    /// </summary>
    public static OcrResult Empty { get; } = new()
    {
        FullText = string.Empty,
        Lines = Array.Empty<OcrLine>(),
        Languages = Array.Empty<string>(),
        Duration = TimeSpan.Zero,
        UsedGpu = false
    };
}
