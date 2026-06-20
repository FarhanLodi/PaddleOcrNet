using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PaddleOcrNet.Structure.Preprocess;

/// <summary>
/// Document pre-processor. When enabled, runs the PP-LCNet document-orientation classifier (0/90/180/270°)
/// to upright the page and/or the UVDoc unwarp model to dewarp it, before layout detection. Either model
/// may be absent (<c>null</c> session), in which case that stage is skipped. Owns and disposes both
/// optional sessions.
/// <para>
/// <b>Implementation status:</b> the orientation stage is <b>fully implemented</b> (PP-LCNet doc-ori →
/// argmax → in-place 90° rotation). The UVDoc unwarp stage is a <b>safe pass-through stub</b> — when
/// requested it returns the image unchanged and logs nothing; see <see cref="Unwarp"/> for the rationale
/// and the TODO describing the remaining work. This keeps the pipeline correct (unwarp simply has no
/// effect) rather than risking an incorrect dewarp from an unverified grid convention.
/// </para>
/// Reference: PaddleX <c>doc_orientation_classify</c> (PP-LCNet_x1_0_doc_ori) and <c>UVDoc</c> dewarp.
/// </summary>
internal sealed class DocPreprocessor : IDocPreprocessor
{
    // ImageNet mean/std (RGB), applied to pixel/255 — the PP-LCNet doc-ori classifier's normalization.
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    // PP-LCNet_x1_0_doc_ori input is a fixed 3×224×224 (standard PP-LCNet classification head).
    private const int OrientSize = 224;

    // Output index -> clockwise rotation (degrees) PaddleX assigns to the page. Index i means "the page is
    // currently rotated by OrientationAngles[i]° clockwise from upright"; we rotate by the inverse to correct.
    private static readonly int[] OrientationAngles = { 0, 90, 180, 270 };

    private readonly InferenceSession? _orientation;
    private readonly InferenceSession? _unwarp;
    private readonly string? _orientationInputName;

    /// <summary>
    /// Creates the pre-processor over optional orientation / unwarp ONNX sessions.
    /// </summary>
    /// <param name="orientation">The doc-orientation classifier session, or <c>null</c> to disable orientation.</param>
    /// <param name="unwarp">The UVDoc unwarp session, or <c>null</c> to disable unwarping.</param>
    public DocPreprocessor(InferenceSession? orientation, InferenceSession? unwarp)
    {
        _orientation = orientation;
        _unwarp = unwarp;
        // The doc-ori graph has a single input; resolve its name once when present.
        _orientationInputName = orientation?.InputMetadata.Keys.First();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Order matches PaddleX's document pipeline: orientation correction first (so unwarp sees an upright
    /// page), then unwarp. The returned image is always a fresh image the caller owns and disposes — even
    /// when no stage changes the pixels we hand back a clone so callers can dispose the input independently.
    /// </remarks>
    public (Image<Rgb24> image, int rotationApplied) Apply(Image<Rgb24> input, bool useOrientation, bool useUnwarp)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Work on an owned copy so the input remains the caller's to dispose and intermediate stages can be
        // swapped without aliasing. Each stage replaces 'current' and disposes the one it consumed.
        Image<Rgb24> current = input.Clone();
        int rotationApplied = 0;

        try
        {
            // --- Orientation: classify {0,90,180,270} and rotate the whole page upright (FULLY IMPLEMENTED).
            if (useOrientation && _orientation is not null)
            {
                rotationApplied = ClassifyAndRotate(current);
            }

            // --- Unwarp: UVDoc dewarp (STUB — safe pass-through; see Unwarp()).
            if (useUnwarp && _unwarp is not null)
            {
                Image<Rgb24> dewarped = Unwarp(current);
                if (!ReferenceEquals(dewarped, current))
                {
                    current.Dispose();
                    current = dewarped;
                }
            }

            return (current, rotationApplied);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs the PP-LCNet doc-orientation classifier on <paramref name="image"/>, takes the argmax over the
    /// four {0,90,180,270} logits, and rotates the image in place to upright it. Returns the rotation (in
    /// degrees, clockwise) that was actually applied to the image (0 when the page is already upright).
    /// </summary>
    /// <remarks>
    /// PaddleX labels index <c>i</c> with <see cref="OrientationAngles"/>[i] = the clockwise angle by which
    /// the page is currently rotated away from upright. To correct it we rotate by the same magnitude in the
    /// opposite direction: a page predicted as "90° clockwise" is rotated 270° clockwise (= −90°) back to
    /// upright. We report the applied (corrective) clockwise rotation so the caller can map any downstream
    /// coordinates back to the original page if needed.
    /// </remarks>
    private int ClassifyAndRotate(Image<Rgb24> image)
    {
        var input = BuildOrientationTensor(image);

        using var results = _orientation!.Run(
            new[] { NamedOnnxValue.CreateFromTensor(_orientationInputName!, input) });

        // Output is [1, 4]: logits/probabilities over {0°, 90°, 180°, 270°}. Take the argmax label.
        var scores = results[0].AsEnumerable<float>().ToArray();
        int label = ArgMax(scores);
        int detected = label >= 0 && label < OrientationAngles.Length ? OrientationAngles[label] : 0;
        if (detected == 0)
        {
            return 0;
        }

        // Corrective clockwise rotation = 360 - detected (e.g. detected 90 -> rotate 270 CW to upright).
        int correction = (360 - detected) % 360;
        var mode = correction switch
        {
            90 => RotateMode.Rotate90,
            180 => RotateMode.Rotate180,
            270 => RotateMode.Rotate270,
            _ => RotateMode.None,
        };
        if (mode != RotateMode.None)
        {
            image.Mutate(ctx => ctx.Rotate(mode));
        }
        return correction;
    }

    /// <summary>
    /// Preprocesses <paramref name="image"/> into the doc-ori model's <c>[1,3,224,224]</c> input: resize
    /// (stretch) to 224×224, ImageNet-normalize <c>(pixel/255 - mean)/std</c> in RGB order, CHW layout.
    /// </summary>
    private static DenseTensor<float> BuildOrientationTensor(Image<Rgb24> image)
    {
        using var resized = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(OrientSize, OrientSize),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        var tensor = new DenseTensor<float>(new[] { 1, 3, OrientSize, OrientSize });
        int plane = OrientSize * OrientSize;
        Memory<float> bufferMem = tensor.Buffer;

        resized.ProcessPixelRows(accessor =>
        {
            var buffer = bufferMem.Span;
            for (int y = 0; y < OrientSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = y * OrientSize;
                for (int x = 0; x < OrientSize; x++)
                {
                    var px = row[x];
                    int idx = rowOffset + x;
                    buffer[idx] = (px.R / 255f - Mean[0]) / Std[0];            // R channel
                    buffer[plane + idx] = (px.G / 255f - Mean[1]) / Std[1];     // G channel
                    buffer[2 * plane + idx] = (px.B / 255f - Mean[2]) / Std[2]; // B channel
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// UVDoc document unwarp — <b>STUB: returns the input unchanged.</b>
    /// </summary>
    /// <remarks>
    /// TODO (unwarp not implemented): UVDoc predicts a backward sampling grid (a flow field, typically
    /// shaped <c>[1, 2, Hg, Wg]</c> in the normalized [-1, 1] <c>grid_sample</c> convention) from the page
    /// resized to the network's fixed input size (read from <c>_unwarp.InputMetadata</c>, e.g. ~488×712).
    /// A correct implementation would: (1) resize the page to that size and ImageNet/[0,1]-normalize it;
    /// (2) run the net to get the grid; (3) bilinearly upsample the grid to the output resolution; and
    /// (4) for each output pixel, bilinearly sample the source page at the grid-specified (x, y) — the
    /// standard <c>grid_sample</c> remap. The exact grid orientation (forward vs backward), value range,
    /// channel order, and output tensor name are model-specific and could not be verified against the
    /// actual UVDoc.onnx export here. Rather than risk corrupting the page with a wrong remap, this stage
    /// is a deliberate, safe pass-through: unwarp has <i>no effect</i> while still being wired and selectable.
    /// Reference: UVDoc (Verhoeven et al.) + PaddleX <c>doc_unwarp</c> using <c>F.grid_sample</c>.
    /// </remarks>
    private Image<Rgb24> Unwarp(Image<Rgb24> image)
    {
        // Intentionally no-op until the UVDoc grid I/O contract is verified against the real model.
        // The session is held (and disposed) so enabling this later only requires filling in the remap.
        _ = _unwarp;
        return image;
    }

    /// <summary>Returns the index of the largest score in <paramref name="scores"/> (0 when empty).</summary>
    private static int ArgMax(ReadOnlySpan<float> scores)
    {
        if (scores.Length == 0) return 0;
        int best = 0;
        float max = scores[0];
        for (int i = 1; i < scores.Length; i++)
        {
            if (scores[i] > max)
            {
                max = scores[i];
                best = i;
            }
        }
        return best;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _orientation?.Dispose();
        _unwarp?.Dispose();
    }
}
