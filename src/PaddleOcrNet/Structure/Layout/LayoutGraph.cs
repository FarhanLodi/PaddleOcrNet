using PaddleOcrNet.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PaddleOcrNet.Structure.Layout;

/// <summary>
/// Shared ONNX-graph helpers for the PaddleX / PaddleOCR layout detectors (<see cref="PicoDetLayoutDetector"/>
/// and <see cref="RtDetrLayoutDetector"/>). Both exports fuse the box decode + NMS into the graph and emit
/// the same already-decoded detection layout — <c>[N, 6]</c> rows of
/// <c>[class_id, score, x1, y1, x2, y2]</c> plus an <c>int32</c> <c>boxes_num</c> tensor giving the per-image
/// row count (batch = 1). This class centralizes (a) resolving the image input name + fixed spatial size and
/// the optional auxiliary inputs from the session metadata, (b) reading those fused detections out of the
/// run results, and (c) thresholding + class-mapping + box-scaling them into <see cref="LayoutRegion"/>s.
/// </summary>
internal static class LayoutGraph
{
    /// <summary>The fused-decode detection row width: <c>[class_id, score, x1, y1, x2, y2]</c>.</summary>
    private const int RowWidth = 6;

    /// <summary>
    /// A flat view over the graph's fused-decode output: <see cref="Rows"/> detections, each a
    /// <see cref="RowWidth"/>-wide run of <c>[class_id, score, x1, y1, x2, y2]</c> in <see cref="Data"/>.
    /// </summary>
    public readonly struct Detections
    {
        /// <summary>The contiguous <c>[Rows × 6]</c> detection values, row-major.</summary>
        public readonly float[] Data;

        /// <summary>The number of valid detection rows.</summary>
        public readonly int Rows;

        public Detections(float[] data, int rows)
        {
            Data = data;
            Rows = rows;
        }
    }

    /// <summary>
    /// Resolves the model's single image input: returns its name and reports its fixed spatial size via
    /// <paramref name="height"/>/<paramref name="width"/>. The image input is the first 4-D
    /// (<c>[N,C,H,W]</c>) input; its H/W come from the graph metadata, falling back to
    /// <paramref name="defaultSize"/> for any dimension the export left dynamic (≤ 0).
    /// </summary>
    public static string ResolveImageInput(InferenceSession session, out int height, out int width, int defaultSize)
    {
        height = defaultSize;
        width = defaultSize;

        foreach (var kvp in session.InputMetadata)
        {
            var dims = kvp.Value.Dimensions;
            if (dims is { Length: 4 })
            {
                int h = dims[2];
                int w = dims[3];
                height = h > 0 ? h : defaultSize;
                width = w > 0 ? w : defaultSize;
                return kvp.Key;
            }
        }

        // No 4-D input found (unexpected): fall back to the first input and the default square size.
        return session.InputMetadata.Keys.First();
    }

    /// <summary>
    /// Returns the graph input whose name equals (case-insensitively) or contains <paramref name="name"/>,
    /// or <c>null</c> when the export does not declare it (e.g. a PicoDet build with no <c>scale_factor</c>).
    /// </summary>
    public static string? FindInput(InferenceSession session, string name)
    {
        foreach (var key in session.InputMetadata.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) ||
                key.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the fused-decode detections out of the run <paramref name="results"/>. Picks the first float
    /// output whose element count is a multiple of <see cref="RowWidth"/> as the <c>[N,6]</c> detection
    /// tensor, and — when present — uses the companion <c>int32</c> <c>boxes_num</c> tensor to cap the row
    /// count to the actual number of valid boxes (otherwise every <c>[N,6]</c> row is taken).
    /// </summary>
    public static Detections ReadDetections(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        float[]? data = null;
        int? boxesNum = null;

        foreach (var value in results)
        {
            // The companion count tensor ("boxes_num") is an int32/int64 [batch] tensor: read its first entry.
            if (boxesNum is null && TryReadCount(value, out int count))
            {
                boxesNum = count;
                continue;
            }

            if (data is null && value.Value is Tensor<float> floatTensor)
            {
                var arr = floatTensor.ToArray();
                if (arr.Length % RowWidth == 0 && arr.Length > 0)
                {
                    data = arr;
                }
            }
        }

        if (data is null)
        {
            return new Detections(Array.Empty<float>(), 0);
        }

        int totalRows = data.Length / RowWidth;
        int rows = boxesNum is int n && n >= 0 ? Math.Min(n, totalRows) : totalRows;
        return new Detections(data, rows);
    }

    /// <summary>
    /// Thresholds, class-maps, and scales the fused detections into <see cref="LayoutRegion"/>s. Each row is
    /// kept when its score exceeds <paramref name="scoreThreshold"/>; its box corners are scaled by
    /// (<paramref name="scaleX"/>, <paramref name="scaleY"/>) into source-image space (1,1 when the graph
    /// already emitted source pixels) and clamped to <paramref name="origW"/>×<paramref name="origH"/>; and
    /// its raw class id is mapped via <paramref name="classMap"/> (unmapped ids resolve to
    /// <see cref="StructureBlockType.Unknown"/>). Zero-area boxes are dropped.
    /// </summary>
    public static IReadOnlyList<LayoutRegion> BuildRegions(
        Detections detections,
        IReadOnlyDictionary<int, StructureBlockType> classMap,
        float scoreThreshold,
        float scaleX,
        float scaleY,
        int origW,
        int origH)
    {
        var data = detections.Data;
        var regions = new List<LayoutRegion>();

        for (int i = 0; i < detections.Rows; i++)
        {
            int baseIdx = i * RowWidth;
            float score = data[baseIdx + 1];
            if (score <= scoreThreshold)
            {
                continue;
            }

            int rawClassId = (int)MathF.Round(data[baseIdx]);

            // Scale box corners from the graph's output space into source-image pixels, then clamp.
            float x1 = data[baseIdx + 2] * scaleX;
            float y1 = data[baseIdx + 3] * scaleY;
            float x2 = data[baseIdx + 4] * scaleX;
            float y2 = data[baseIdx + 5] * scaleY;

            double minX = Math.Clamp(Math.Min(x1, x2), 0, origW);
            double minY = Math.Clamp(Math.Min(y1, y2), 0, origH);
            double maxX = Math.Clamp(Math.Max(x1, x2), 0, origW);
            double maxY = Math.Clamp(Math.Max(y1, y2), 0, origH);
            if (maxX <= minX || maxY <= minY)
            {
                continue; // degenerate / off-image box
            }

            var type = classMap.TryGetValue(rawClassId, out var mapped) ? mapped : StructureBlockType.Unknown;
            regions.Add(new LayoutRegion(type, new OcrBoundingBox(minX, minY, maxX, maxY), score, rawClassId));
        }

        return regions;
    }

    /// <summary>
    /// Tries to read a scalar count from a <c>boxes_num</c>-style tensor (an <c>int32</c> or <c>int64</c>
    /// tensor of length ≥ 1). Returns the first element; <c>false</c> for float tensors or unsupported types.
    /// </summary>
    private static bool TryReadCount(DisposableNamedOnnxValue value, out int count)
    {
        switch (value.Value)
        {
            case Tensor<int> i32 when i32.Length >= 1:
                count = i32.GetValue(0);
                return true;
            case Tensor<long> i64 when i64.Length >= 1:
                count = (int)i64.GetValue(0);
                return true;
            default:
                count = 0;
                return false;
        }
    }
}
