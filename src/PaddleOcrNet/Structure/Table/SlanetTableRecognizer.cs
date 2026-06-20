using System.Globalization;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PaddleOcrNet.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PaddleOcrNet.Structure.Table;

/// <summary>
/// SLANet / SLANeXt table-structure recognizer. Resizes the table crop to the model's fixed input, runs
/// the structure decoder to produce a sequence of HTML structure tokens (from the supplied
/// <see cref="_structureVocab"/>) plus per-cell bounding boxes, then distributes the supplied OCR lines into
/// the cells (by box overlap) and emits a complete HTML <c>&lt;table&gt;</c>. Owns and disposes the ONNX session.
/// <para>
/// The model is the SLANet attention encoder-decoder exported to ONNX with the autoregressive decoder fully
/// <b>unrolled</b> into the graph: a single <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>
/// returns <b>both</b> the per-step structure-token probabilities and the per-step cell bounding boxes — there
/// is no host-side decode loop. This mirrors PaddleOCR's <c>TableLabelDecode</c> (post-processing) and
/// <c>TableMatch</c> (matcher), and RapidAI/RapidTable's <c>TableStructurer</c>.
/// </para>
/// </summary>
internal sealed class SlanetTableRecognizer : ITableRecognizer
{
    /// <summary>The fixed square input edge SLANet/SLANeXt are exported with (PaddleOCR's <c>table_max_len</c>).</summary>
    private const int InputSize = 488;

    /// <summary>ImageNet per-channel mean (RGB order), the SLANet normalization the model was trained with.</summary>
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };

    /// <summary>ImageNet per-channel std (RGB order).</summary>
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    /// <summary>
    /// The structure tokens whose appearance in the decoded sequence consumes one bounding-box row from the
    /// location head. Matches PaddleOCR's <c>TableLabelDecode.td_token</c>: a cell is opened by exactly one of
    /// <c>&lt;td&gt;</c>, <c>&lt;td</c> (the prefix of a span-cell whose attributes follow) or the merged
    /// <c>&lt;td&gt;&lt;/td&gt;</c> token.
    /// </summary>
    private static readonly HashSet<string> TdTokens = new(StringComparer.Ordinal)
    {
        "<td>", "<td", "<td></td>",
    };

    private readonly InferenceSession _session;

    /// <summary>
    /// The full token vocabulary the structure head emits, indexed by class id. Built as
    /// <c>["sos"] + table_structure_dict.txt tokens + ["eos"]</c> so index 0 is the start token and the last
    /// index is the end token (see <see cref="BuildVocab"/>).
    /// </summary>
    private readonly IReadOnlyList<string> _structureVocab;

    /// <summary>Class id of the start-of-sequence token (always 0, the first vocab entry).</summary>
    private readonly int _begIdx;

    /// <summary>Class id of the end-of-sequence token (always the last vocab entry).</summary>
    private readonly int _endIdx;

    private readonly string _inputName;

    /// <summary>
    /// Creates the recognizer over a built SLANet/SLANeXt ONNX session and the structure-token vocabulary
    /// (from <c>table_structure_dict.txt</c>) the decoder's head emits.
    /// </summary>
    /// <param name="session">The loaded table-structure model session (this instance takes ownership).</param>
    /// <param name="structureVocab">The ordered structure-token vocabulary (HTML tag tokens + control tokens).</param>
    public SlanetTableRecognizer(InferenceSession session, IReadOnlyList<string> structureVocab)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(structureVocab);

        _structureVocab = BuildVocab(structureVocab);
        _begIdx = 0;
        _endIdx = _structureVocab.Count - 1;
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <inheritdoc />
    public TableResult Recognize(Image<Rgb24> tableCrop, IReadOnlyList<OcrLine> ocrLines)
    {
        ArgumentNullException.ThrowIfNull(tableCrop);
        ocrLines ??= Array.Empty<OcrLine>();

        // 1. Pre-process: aspect-preserving resize → ImageNet normalize → CHW → top-left pad to 488×488. The
        //    shape_list carries [origH, origW, ratio, ratio] so the location head's [0,1] boxes can be mapped
        //    back into the crop's pixel coordinates.
        var input = Preprocess(tableCrop, out int origW, out int origH, out float ratio);

        // 2. Run the unrolled decoder graph once — it returns BOTH the location boxes and the structure probs.
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) };
        using var outputs = _session.Run(inputs);

        // out[0] = loc_preds  : float [1, L, 4] sigmoid (x1,y1,x2,y2) in [0,1]
        // out[1] = structure_probs : float [1, L, C] softmax over the C-class vocab
        var outList = outputs.ToList();
        var (locTensor, probTensor) = ResolveOutputs(outList);

        // 3. Decode the structure-token sequence and the per-cell boxes (rescaled to crop pixels).
        var (structureTokens, cellBoxes) = Decode(locTensor, probTensor, origW, origH, ratio);

        // 4. Match each OCR line to its best predicted cell, then weave the text into the HTML skeleton.
        var matched = MatchOcrToCells(cellBoxes, ocrLines);
        string html = BuildHtml(structureTokens, matched);

        var cellBounds = new OcrBoundingBox[cellBoxes.Count];
        for (int i = 0; i < cellBoxes.Count; i++)
        {
            var b = cellBoxes[i];
            cellBounds[i] = new OcrBoundingBox(b[0], b[1], b[2], b[3]);
        }

        return new TableResult(html, cellBounds);
    }

    // ===============================================================================================
    // Pre-processing
    // ===============================================================================================

    /// <summary>
    /// Builds the SLANet input tensor. Replicates PaddleOCR's <c>ResizeTableImage</c> +
    /// <c>NormalizeImage</c> + <c>PaddingTableImage</c>: scale by <c>ratio = 488 / max(h,w)</c> (so the long
    /// edge becomes ≤488 and aspect ratio is preserved), normalize each RGB channel with the ImageNet
    /// mean/std, lay out planar CHW, and pad the bottom/right with zeros to a fixed 488×488 (the image sits in
    /// the top-left corner).
    /// </summary>
    /// <param name="crop">The table crop (caller retains ownership).</param>
    /// <param name="origW">The crop's original pixel width (for the shape_list / box rescale).</param>
    /// <param name="origH">The crop's original pixel height.</param>
    /// <param name="ratio">The applied resize ratio (long-edge scale).</param>
    private static DenseTensor<float> Preprocess(Image<Rgb24> crop, out int origW, out int origH, out float ratio)
    {
        origW = crop.Width;
        origH = crop.Height;

        int longEdge = Math.Max(origH, origW);
        ratio = longEdge > 0 ? InputSize / (float)longEdge : 1f;

        // PaddleOCR uses int(ratio * dim) for the resized extent, clamped to at least 1px.
        int resizeW = Math.Max(1, Math.Min(InputSize, (int)(ratio * origW)));
        int resizeH = Math.Max(1, Math.Min(InputSize, (int)(ratio * origH)));

        using var resized = crop.Clone(c => c.Resize(new ResizeOptions
        {
            Size = new Size(resizeW, resizeH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        // Planar CHW [3·488·488], zero-initialized → the pad region (bottom/right) stays zero. We fill a
        // plain float[] here (the DenseTensor's Span is a ref struct and cannot be captured by the
        // ProcessPixelRows lambda), then wrap it as the tensor.
        int plane = InputSize * InputSize;
        var data = new float[3 * plane];

        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < resizeH; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                int rowBase = y * InputSize;
                for (int x = 0; x < resizeW; x++)
                {
                    Rgb24 px = row[x];
                    // (x/255 - mean) / std, per channel, ImageNet statistics.
                    float r = (px.R / 255f - Mean[0]) / Std[0];
                    float g = (px.G / 255f - Mean[1]) / Std[1];
                    float b = (px.B / 255f - Mean[2]) / Std[2];
                    int p = rowBase + x;
                    data[p] = r;                 // channel 0 (R)
                    data[plane + p] = g;         // channel 1 (G)
                    data[2 * plane + p] = b;     // channel 2 (B)
                }
            }
        });

        // [1, 3, 488, 488].
        return new DenseTensor<float>(data, new[] { 1, 3, InputSize, InputSize });
    }

    // ===============================================================================================
    // Decode
    // ===============================================================================================

    /// <summary>
    /// Decodes the unrolled graph outputs into (a) the HTML structure-token list and (b) the per-cell
    /// bounding boxes in the crop's pixel coordinates. Mirrors PaddleOCR's <c>TableLabelDecode.decode</c>
    /// verbatim: take the per-step argmax of the structure probabilities, then for each step <c>idx</c>:
    /// break when <c>idx &gt; 0 &amp;&amp; char == eos</c>; skip the ignored tokens (sos and eos); for every
    /// td-token (<see cref="TdTokens"/>) decode and collect the matching location box; append the token to the
    /// structure list. The box is rescaled by <c>_bbox_decode</c>: each coordinate is multiplied by the padded
    /// canvas edge (488) and divided by the resize ratio, landing it back in the original crop's pixels.
    /// </summary>
    /// <param name="locTensor">Location head, shape [1, L, 4], sigmoid (x1,y1,x2,y2) in [0,1].</param>
    /// <param name="probTensor">Structure head, shape [1, L, C], softmax over the vocab.</param>
    /// <param name="origW">Crop original width (for clamping the decoded boxes).</param>
    /// <param name="origH">Crop original height (for clamping the decoded boxes).</param>
    /// <param name="ratio">Resize ratio <c>488 / max(h,w)</c> (the shape_list's ratio_h == ratio_w).</param>
    private (List<string> Tokens, List<float[]> Boxes) Decode(
        Tensor<float> locTensor,
        Tensor<float> probTensor,
        int origW,
        int origH,
        float ratio)
    {
        var probDims = probTensor.Dimensions;
        int steps = probDims[1];
        int numClasses = probDims[2];

        var locDims = locTensor.Dimensions;
        int boxSteps = locDims[1];

        // Flatten once so we can index without the per-element tensor-indexer overhead.
        float[] probs = probTensor.ToArray();
        float[] loc = locTensor.ToArray();

        // _bbox_decode: bbox[0::2] *= pad_w; bbox[1::2] *= pad_h; bbox[0::2] /= ratio_w; bbox[1::2] /= ratio_h.
        // Here pad_w == pad_h == InputSize (488) and ratio_w == ratio_h == ratio, so every coordinate is
        // scaled by (InputSize / ratio) == max(origH, origW). Guard against a zero ratio.
        float safeRatio = ratio > 0 ? ratio : 1f;
        float xScale = InputSize / safeRatio;
        float yScale = InputSize / safeRatio;

        var tokens = new List<string>(steps);
        var boxes = new List<float[]>();

        for (int t = 0; t < steps; t++)
        {
            // argmax over the C classes for step t.
            int baseIdx = t * numClasses;
            int best = 0;
            float bestVal = probs[baseIdx];
            for (int c = 1; c < numClasses; c++)
            {
                float v = probs[baseIdx + c];
                if (v > bestVal)
                {
                    bestVal = v;
                    best = c;
                }
            }

            // PaddleOCR: `if idx > 0 and char_idx == end_idx: break`. (The eos at step 0 is not a break; it
            // is then dropped by the ignored-token skip below.)
            if (t > 0 && best == _endIdx)
            {
                break;
            }

            // ignored_tokens == {beg_idx, end_idx} — skip both without consuming a box or emitting a token.
            if (best == _begIdx || best == _endIdx)
            {
                continue;
            }

            string token = best >= 0 && best < _structureVocab.Count ? _structureVocab[best] : string.Empty;

            // td-tokens decode and collect the location box at THIS step (lock-step with the structure step).
            if (TdTokens.Contains(token) && t < boxSteps)
            {
                int lBase = t * 4;
                float x1 = loc[lBase + 0] * xScale;
                float y1 = loc[lBase + 1] * yScale;
                float x2 = loc[lBase + 2] * xScale;
                float y2 = loc[lBase + 3] * yScale;

                // Clamp into the crop so downstream overlays / matching stay inside the image.
                x1 = Math.Clamp(x1, 0f, origW);
                y1 = Math.Clamp(y1, 0f, origH);
                x2 = Math.Clamp(x2, 0f, origW);
                y2 = Math.Clamp(y2, 0f, origH);
                boxes.Add(new[] { x1, y1, x2, y2 });
            }

            // Every non-ignored token (td or not) goes into the structure list, matching PaddleOCR.
            tokens.Add(token);
        }

        return (tokens, boxes);
    }

    // ===============================================================================================
    // OCR ↔ cell matching  (PaddleOCR matcher.py / TableMatch)
    // ===============================================================================================

    /// <summary>
    /// For each OCR line, finds the predicted cell it belongs to. Replicates PaddleOCR's
    /// <c>TableMatch.match_result</c> exactly: a line (ground-truth box) is assigned to the predicted cell
    /// minimizing the pair <c>(1 − IoU, corner-distance)</c> compared lexicographically — the IoU term is the
    /// <b>primary</b> key and the corner distance is the tiebreaker (PaddleOCR sorts by
    /// <c>key=lambda item: (item[1], item[0])</c> over <c>(distance, 1 − iou)</c>). Returns, per predicted
    /// cell index, the OCR texts (in OCR order) assigned to it.
    /// </summary>
    /// <param name="cellBoxes">Predicted cell boxes in crop pixels, in structure (HTML) order.</param>
    /// <param name="ocrLines">OCR lines in the crop's pixel coordinates.</param>
    /// <returns>cell index → list of OCR texts assigned to that cell.</returns>
    private static Dictionary<int, List<string>> MatchOcrToCells(
        IReadOnlyList<float[]> cellBoxes,
        IReadOnlyList<OcrLine> ocrLines)
    {
        var matched = new Dictionary<int, List<string>>();
        if (cellBoxes.Count == 0)
        {
            return matched;
        }

        foreach (var line in ocrLines)
        {
            if (string.IsNullOrEmpty(line.Text))
            {
                continue;
            }

            var ob = line.BoundingBox;
            float[] gtBox = { (float)ob.MinX, (float)ob.MinY, (float)ob.MaxX, (float)ob.MaxY };

            int bestCell = -1;
            float bestIouComplement = float.MaxValue; // primary sort key (item[1] = 1 − IoU)
            float bestDistance = float.MaxValue;       // secondary sort key (item[0] = corner distance)

            for (int j = 0; j < cellBoxes.Count; j++)
            {
                float[] predBox = cellBoxes[j];

                float dist = CornerDistance(gtBox, predBox);          // PaddleOCR's distance(), item[0]
                float iouComplement = 1f - ComputeIou(gtBox, predBox); // 1 − iou,                  item[1]

                // sorted(..., key=lambda item: (item[1], item[0])) then take index 0 ⇒ minimize
                // (iouComplement, dist) lexicographically. Strict < keeps the FIRST cell on ties, matching
                // Python's stable sort.
                if (iouComplement < bestIouComplement ||
                    (iouComplement == bestIouComplement && dist < bestDistance))
                {
                    bestIouComplement = iouComplement;
                    bestDistance = dist;
                    bestCell = j;
                }
            }

            if (bestCell >= 0)
            {
                if (!matched.TryGetValue(bestCell, out var texts))
                {
                    texts = new List<string>();
                    matched[bestCell] = texts;
                }

                texts.Add(line.Text);
            }
        }

        return matched;
    }

    /// <summary>
    /// PaddleOCR's <c>distance(box_1, box_2)</c>: the full corner L1 distance
    /// (<c>|x3−x1|+|y3−y1|+|x4−x2|+|y4−y2|</c>) plus the smaller of the two single-corner sub-distances
    /// (top-left <c>|x3−x1|+|y3−y1|</c> vs bottom-right <c>|x4−x2|+|y4−y2|</c>). Boxes are [x1,y1,x2,y2].
    /// </summary>
    private static float CornerDistance(float[] box1, float[] box2)
    {
        float dis = Math.Abs(box2[0] - box1[0]) + Math.Abs(box2[1] - box1[1])
                  + Math.Abs(box2[2] - box1[2]) + Math.Abs(box2[3] - box1[3]);
        float dis2 = Math.Abs(box2[0] - box1[0]) + Math.Abs(box2[1] - box1[1]); // top-left corner
        float dis3 = Math.Abs(box2[2] - box1[2]) + Math.Abs(box2[3] - box1[3]); // bottom-right corner
        return dis + Math.Min(dis2, dis3);
    }

    /// <summary>
    /// Intersection-over-union of two axis-aligned boxes given as [x1,y1,x2,y2]. Matches PaddleOCR's
    /// <c>compute_iou</c> verbatim, including its <c>&gt;=</c> no-overlap boundary check (touching boxes
    /// yield 0). Per-component intersection math is symmetric in x/y, so the [x1,y1,x2,y2] order used here is
    /// equivalent to PaddleOCR's index pairing.
    /// </summary>
    private static float ComputeIou(float[] rec1, float[] rec2)
    {
        float area1 = (rec1[2] - rec1[0]) * (rec1[3] - rec1[1]);
        float area2 = (rec2[2] - rec2[0]) * (rec2[3] - rec2[1]);
        float sumArea = area1 + area2;

        float left = Math.Max(rec1[0], rec2[0]);
        float right = Math.Min(rec1[2], rec2[2]);
        float top = Math.Max(rec1[1], rec2[1]);
        float bottom = Math.Min(rec1[3], rec2[3]);

        // PaddleOCR uses >= here: exactly-touching boxes count as no overlap.
        if (left >= right || top >= bottom)
        {
            return 0f;
        }

        float intersect = (right - left) * (bottom - top);
        float union = sumArea - intersect;
        return union <= 0f ? 0f : intersect / union;
    }

    // ===============================================================================================
    // HTML assembly  (PaddleOCR matcher.py get_pred_html)
    // ===============================================================================================

    /// <summary>
    /// Assembles the final table HTML from the structure tokens, inserting each cell's matched OCR text at the
    /// cell-content position. Exactly mirrors PaddleOCR's <c>TableMatch.get_pred_html</c>: walk the structure
    /// tokens with a running cell index <c>td_index</c> that advances on every tag that <b>contains</b>
    /// <c>&lt;/td&gt;</c> (i.e. the <c>&lt;/td&gt;</c> closer and the merged <c>&lt;td&gt;&lt;/td&gt;</c>); for
    /// the merged token a synthetic <c>&lt;td&gt;</c> is emitted first, then the cell's text, then the closer.
    /// All other tokens (openers like <c>&lt;td&gt;</c>/<c>&lt;td</c>, the <c>&gt;</c> closer, and the table/
    /// row/section tags) pass through verbatim. The stream is wrapped in
    /// <c>&lt;html&gt;&lt;body&gt;&lt;table&gt;…&lt;/table&gt;&lt;/body&gt;&lt;/html&gt;</c>.
    /// <para>
    /// The <c>td_index</c> here is the index of the Nth <em>closed</em> cell, which equals the index of the
    /// Nth box collected in <see cref="Decode"/> (each td-opener box is paired 1:1 with its closer in a
    /// well-formed sequence), so the matched-text dictionary keyed by box index lines up.
    /// </para>
    /// </summary>
    /// <param name="structureTokens">Decoded structure tokens in document order.</param>
    /// <param name="matched">cell index → OCR texts (from <see cref="MatchOcrToCells"/>).</param>
    private static string BuildHtml(IReadOnlyList<string> structureTokens, IReadOnlyDictionary<int, List<string>> matched)
    {
        var sb = new StringBuilder();
        sb.Append("<html><body><table>");

        int tdIndex = 0;
        foreach (string tag in structureTokens)
        {
            // PaddleOCR keys the cell content on tags that CONTAIN "</td>": the bare "</td>" closer and the
            // merged "<td></td>". Everything else is appended verbatim.
            if (tag.Contains("</td>", StringComparison.Ordinal))
            {
                if (tag == "<td></td>")
                {
                    sb.Append("<td>");
                }

                AppendCellText(sb, matched, tdIndex);

                if (tag == "<td></td>")
                {
                    sb.Append("</td>");
                }
                else
                {
                    sb.Append(tag);
                }

                tdIndex++;
            }
            else
            {
                sb.Append(tag);
            }
        }

        sb.Append("</table></body></html>");
        return sb.ToString();
    }

    /// <summary>Appends the OCR text matched to <paramref name="cellIndex"/> (HTML-escaped), if any.</summary>
    private static void AppendCellText(StringBuilder sb, IReadOnlyDictionary<int, List<string>> matched, int cellIndex)
    {
        if (cellIndex < 0 || !matched.TryGetValue(cellIndex, out var texts) || texts.Count == 0)
        {
            return;
        }

        for (int i = 0; i < texts.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(HtmlEscape(texts[i]));
        }
    }

    /// <summary>Minimal HTML entity escaping for the cell text (so OCR text never breaks the markup).</summary>
    private static string HtmlEscape(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    // ===============================================================================================
    // Vocab / output wiring
    // ===============================================================================================

    /// <summary>
    /// Builds the full class-indexed vocabulary the structure head emits, replicating PaddleOCR's
    /// <c>TableLabelDecode.__init__</c> exactly:
    /// <list type="number">
    /// <item>load the <c>table_structure_dict.txt</c> tokens (one per line);</item>
    /// <item>apply the <c>merge_no_span_structure</c> transform the SLANet_plus head was trained with —
    /// append <c>&lt;td&gt;&lt;/td&gt;</c> if absent, then remove <c>&lt;td&gt;</c> if present;</item>
    /// <item>wrap with the special tokens via <c>add_special_char</c>:
    /// <c>["sos"] + dict + ["eos"]</c>.</item>
    /// </list>
    /// This yields a 30-class head: index 0 = <c>sos</c>, last index = <c>eos</c>. If the caller already
    /// supplied a wrapped/merged list it is returned unchanged (idempotent).
    /// </summary>
    private static IReadOnlyList<string> BuildVocab(IReadOnlyList<string> dict)
    {
        // Copy the dict tokens, dropping any trailing blank line File.ReadAllLines yields from a final newline.
        var tokens = new List<string>(dict.Count + 2);
        foreach (var line in dict)
        {
            tokens.Add(line);
        }

        while (tokens.Count > 0 && string.IsNullOrEmpty(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        // Already wrapped with sentinels (e.g. a pre-built 30-class vocab) → return as-is.
        bool hasSentinels = tokens.Count >= 2 &&
            (tokens[0] is "sos" or "beg" or "<sos>") &&
            (tokens[^1] is "eos" or "end" or "<eos>");
        if (hasSentinels)
        {
            return tokens;
        }

        // merge_no_span_structure=True: append "<td></td>" (if missing), then remove "<td>" (if present).
        // The order matters — PaddleOCR appends first, removes second — so "<td></td>" lands at the end.
        if (!tokens.Contains("<td></td>"))
        {
            tokens.Add("<td></td>");
        }

        tokens.Remove("<td>");

        // add_special_char: ["sos"] + dict + ["eos"].
        var vocab = new List<string>(tokens.Count + 2) { "sos" };
        vocab.AddRange(tokens);
        vocab.Add("eos");
        return vocab;
    }

    /// <summary>
    /// Resolves which of the two graph outputs is the location head ([·,·,4]) and which is the structure head
    /// ([·,·,C], C&gt;4). Output ordering can differ between exports, so we identify them by their last
    /// dimension rather than by position.
    /// </summary>
    private static (Tensor<float> Loc, Tensor<float> Prob) ResolveOutputs(List<DisposableNamedOnnxValue> outputs)
    {
        if (outputs.Count < 2)
        {
            throw new InvalidOperationException(
                $"SLANet table model returned {outputs.Count} output(s); expected 2 (loc_preds + structure_probs).");
        }

        var a = outputs[0].AsTensor<float>();
        var b = outputs[1].AsTensor<float>();

        int aLast = a.Dimensions[^1];
        int bLast = b.Dimensions[^1];

        // The location head's last dim is 4 (x1,y1,x2,y2); the structure head's is the vocab size (>4).
        if (aLast == 4 && bLast != 4)
        {
            return (a, b);
        }

        if (bLast == 4 && aLast != 4)
        {
            return (b, a);
        }

        // Fall back to the documented order: out[0]=loc, out[1]=structure.
        return (a, b);
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
