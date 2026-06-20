using PaddleOcrNet.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PaddleOcrNet.Structure.Layout;

/// <summary>
/// PP-DocLayout_plus-L / PP-DocLayout-L layout detector (RT-DETR backbone). Resizes the page to the model's
/// fixed input (PP-DocLayout_plus-L = 800×800, PP-DocLayout-L = 640×640), runs the RT-DETR graph (whose
/// decoder emits a fixed top-k of 300 boxes — there is no NMS), score-thresholds the predictions, and maps
/// each raw class index onto a <see cref="StructureBlockType"/> via the supplied <see cref="_classMap"/>.
/// Owns and disposes the ONNX session.
/// <para>
/// As with the PicoDet export, the decode is fused into the graph: the output is already-decoded detections
/// shaped <c>[300, 6]</c> as <c>[class_id, score, x1, y1, x2, y2]</c> (plus a <c>boxes_num</c> tensor),
/// batch = 1, with the boxes already in <b>absolute source-image pixels</b> (the graph un-scales them using
/// the supplied <c>scale_factor</c>). This detector performs no decode and no NMS — it only pre-processes,
/// runs the session, keeps rows scoring above the threshold, and maps the class id.
/// </para>
/// <para>
/// Pre-processing is the RT-DETR <b>trap</b>: it feeds <i>raw 0–255 float pixels</i> — there is <b>no</b>
/// <c>/255</c> and <b>no</b> mean/std normalization — in RGB CHW order. The graph takes three inputs:
/// <c>image</c> <c>[1,3,H,W]</c>, <c>im_shape</c> <c>[1,2]</c> = <c>[inputH, inputW]</c>, and
/// <c>scale_factor</c> <c>[1,2]</c> = <c>[resizedH/origH, resizedW/origW]</c> (scale_y first). Because the
/// input is a stretch-resize to a square, both scales equal inputEdge/origEdge per axis.
/// Reference: PaddleX <c>DetResize</c> (keep_ratio = false) + <c>RTDETRPostProcess</c>.
/// </para>
/// </summary>
internal sealed class RtDetrLayoutDetector : ILayoutDetector
{
    /// <summary>Detections scoring at or below this confidence are discarded (PaddleX layout <c>threshold</c>).</summary>
    private const float ScoreThreshold = 0.5f;

    /// <summary>Fallback square input edge when the graph declares a dynamic spatial dimension (PP-DocLayout_plus-L default).</summary>
    private const int DefaultInputSize = 800;

    private readonly InferenceSession _session;
    private readonly IReadOnlyDictionary<int, StructureBlockType> _classMap;

    // Resolved once from the graph metadata: the image input name, the model's fixed input H/W, and the
    // auxiliary "im_shape" / "scale_factor" input names (RT-DETR exports always declare both).
    private readonly string _imageInputName;
    private readonly string? _imShapeInputName;
    private readonly string? _scaleFactorInputName;
    private readonly int _inputHeight;
    private readonly int _inputWidth;

    /// <summary>
    /// Creates the detector over a built RT-DETR ONNX session and a raw-class-id → block-type map (parsed
    /// from the model's label .yml / class list).
    /// </summary>
    /// <param name="session">The loaded RT-DETR layout model session (this instance takes ownership).</param>
    /// <param name="classMap">Maps the model's raw class indices to <see cref="StructureBlockType"/>.</param>
    public RtDetrLayoutDetector(InferenceSession session, IReadOnlyDictionary<int, StructureBlockType> classMap)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _classMap = classMap ?? throw new ArgumentNullException(nameof(classMap));

        // RT-DETR layout graphs expose three inputs: the 4-D image plus 2-D "im_shape" and "scale_factor".
        _imageInputName = LayoutGraph.ResolveImageInput(_session, out _inputHeight, out _inputWidth, DefaultInputSize);
        _imShapeInputName = LayoutGraph.FindInput(_session, "im_shape");
        _scaleFactorInputName = LayoutGraph.FindInput(_session, "scale_factor");
    }

    /// <inheritdoc />
    public IReadOnlyList<LayoutRegion> Detect(Image<Rgb24> image)
    {
        ArgumentNullException.ThrowIfNull(image);

        int origW = image.Width;
        int origH = image.Height;
        if (origW == 0 || origH == 0)
        {
            return Array.Empty<LayoutRegion>();
        }

        // PREPROCESS: stretch-resize to the model's square input and pack RAW 0-255 float pixels into
        // [1,3,H,W] RGB CHW. NOTE: deliberately no /255 and no mean/std — that is the RT-DETR trap.
        var input = BuildInputTensor(image);

        // im_shape is the network input size [inputH, inputW]; scale_factor is [resizedH/origH, resizedW/origW]
        // (scale_y FIRST). The graph uses these to emit boxes in absolute *source-image* pixels.
        float scaleY = _inputHeight / (float)origH;
        float scaleX = _inputWidth / (float)origW;

        var imShape = new DenseTensor<float>(new[] { 1, 2 });
        imShape[0, 0] = _inputHeight;
        imShape[0, 1] = _inputWidth;

        var scaleFactor = new DenseTensor<float>(new[] { 1, 2 });
        scaleFactor[0, 0] = scaleY;
        scaleFactor[0, 1] = scaleX;

        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor(_imageInputName, input),
        };
        if (_imShapeInputName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_imShapeInputName, imShape));
        }
        if (_scaleFactorInputName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_scaleFactorInputName, scaleFactor));
        }

        using var results = _session.Run(inputs);

        // POSTPROCESS: read the fused-decode detections [300,6] = [class_id, score, x1,y1,x2,y2] in absolute
        // source pixels, then threshold and map the class id. No NMS — the RT-DETR decoder is NMS-free.
        var detections = LayoutGraph.ReadDetections(results);
        if (detections.Rows == 0)
        {
            return Array.Empty<LayoutRegion>();
        }

        // Boxes are already in source-image pixels (scale_factor un-scaled them in-graph), so no extra scale.
        var regions = LayoutGraph.BuildRegions(
            detections, _classMap, ScoreThreshold, scaleX: 1f, scaleY: 1f, origW, origH);
        return regions;
    }

    /// <summary>
    /// Stretch-resizes <paramref name="image"/> to the model's square input (keep_ratio = false) and packs
    /// the <b>raw</b> 0–255 channel values (as floats, with no normalization) in RGB CHW order into a
    /// <c>[1, 3, H, W]</c> float32 tensor — the RT-DETR pre-processing the graph expects.
    /// </summary>
    private DenseTensor<float> BuildInputTensor(Image<Rgb24> image)
    {
        int w = _inputWidth;
        int h = _inputHeight;

        using var resized = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(w, h),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });
        int plane = h * w;
        Memory<float> bufferMem = tensor.Buffer;

        resized.ProcessPixelRows(accessor =>
        {
            var buffer = bufferMem.Span;
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = y * w;
                for (int x = 0; x < w; x++)
                {
                    var px = row[x];
                    int idx = rowOffset + x;
                    // RAW pixels — NO /255, NO mean/std. RGB CHW.
                    buffer[idx] = px.R;             // R channel
                    buffer[plane + idx] = px.G;     // G channel
                    buffer[2 * plane + idx] = px.B; // B channel
                }
            }
        });

        return tensor;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
