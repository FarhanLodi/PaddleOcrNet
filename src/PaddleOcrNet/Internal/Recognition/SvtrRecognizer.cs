using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PaddleOcrNet.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace PaddleOcrNet.Internal.Recognition;

/// <summary>
/// PaddleOCR text recognizer (SVTR_LCNet / CRNN family). Each crop is resized to a fixed height
/// (typically 48px) keeping aspect ratio, padded to the batch tensor width, normalized, run through the
/// ONNX network to per-timestep logits, then CTC-greedy-decoded against the character dictionary via
/// <see cref="CtcDecoder"/>.
/// <para>
/// Preprocessing matches PaddleOCR's <c>resize_norm_img</c> (PaddleX
/// <c>text_recognition/processors.py</c>): per batch the tensor width is
/// <c>imgW = int(H · max_wh_ratio)</c> where <c>max_wh_ratio = max(320/H, widest crop's w/h)</c>,
/// capped at 3200 px (crops beyond the cap are aspect-squeezed). Each crop is scaled to height
/// <see cref="_imageHeight"/> with bilinear resampling to width <c>min(ceil(H · w/h), imgW)</c> — the
/// widest crop lands on the floored <c>imgW</c> — normalized as <c>(x/255 − 0.5) / 0.5</c> into [−1,1],
/// laid out as a CHW <b>BGR</b> float tensor (the ONNX rec export consumes BGR, per its
/// <c>inference.yml</c> <c>DecodeImage img_mode: BGR</c>), and right-padded with zeros to <c>imgW</c>.
/// The batch overload sorts crops by aspect ratio (width/height) so similarly-shaped lines share a
/// tensor with minimal padding, runs them in chunks of <c>rec_batch_num</c> (default 6), and reorders
/// the results back to the caller's input order.
/// </para>
/// <para>
/// The network output is <c>[N, T, C]</c> (N rows, T timesteps, C = vocab classes). Each row is handed to
/// <see cref="CtcDecoder.GreedyDecode"/> with the Paddle vocab (blank at index 0). The recognizer returns
/// every result regardless of confidence; the engine applies <c>drop_score</c>.
/// </para>
/// </summary>
internal sealed class SvtrRecognizer : ITextRecognizer
{
    /// <summary>
    /// PaddleOCR's default recognition batch size (<c>rec_batch_num</c>).
    /// </summary>
    private const int DefaultBatchSize = 6;

    /// <summary>
    /// Minimum batch tensor width in pixels. PaddleOCR's <c>rec_image_shape</c> is [3, 48, 320]:
    /// <c>max_wh_ratio</c> starts at 320/H, so every batch tensor is at least 320 px wide.
    /// </summary>
    private const int MinTensorWidth = 320;

    /// <summary>
    /// Maximum batch tensor width in pixels (PaddleOCR's <c>max_imgW</c>). Wider crops are
    /// aspect-squeezed down to this width rather than growing the tensor.
    /// </summary>
    private const int MaxTensorWidth = 3200;

    private readonly InferenceSession _session;
    private readonly IReadOnlyList<string> _dictLines;
    // Built lazily on the first decode, once the model's actual output class count is known, so the vocab
    // length matches the network exactly (community dicts disagree on whether the blank/space are included).
    private IReadOnlyList<string>? _vocab;
    private readonly int _imageHeight;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _batchSize;

    // The most recently requested character filter. The recognizer is shared/cached across calls, while
    // Allowlist/Blocklist are per-call options, so the engine sets this immediately before invoking
    // Recognize, or passes options to the options-aware overload. Null = no filtering.
    private IReadOnlyCollection<string>? _allowlist;
    private IReadOnlyCollection<string>? _blocklist;

    /// <summary>
    /// Creates the recognizer over an already-built ONNX <see cref="InferenceSession"/> and a loaded
    /// character dictionary in the Paddle vocab convention (blank at index 0).
    /// </summary>
    /// <param name="session">The loaded recognition model session (this instance takes ownership and disposes it).</param>
    /// <param name="dictLines">
    /// The raw dictionary lines in CTC index order (see <see cref="CharacterDictionary.LoadLines(string)"/>).
    /// The final vocabulary is built on first inference via <see cref="CharacterDictionary.BuildVocab"/> so its
    /// length matches the model's output class count exactly.
    /// </param>
    /// <param name="imageHeight">Fixed recognition input height in pixels (PaddleOCR's <c>rec_image_shape</c> H). Default 48.</param>
    /// <param name="batchSize">Crops per ONNX run (PaddleOCR's <c>rec_batch_num</c>). Default 6.</param>
    public SvtrRecognizer(InferenceSession session, IReadOnlyList<string> dictLines, int imageHeight = 48, int batchSize = DefaultBatchSize)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dictLines = dictLines ?? throw new ArgumentNullException(nameof(dictLines));
        _imageHeight = imageHeight > 0 ? imageHeight : 48;
        _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;

        // The recognition graph has a single input and a single output; resolve their names once.
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    /// <summary>
    /// Applies the per-call character filter (<see cref="RecognitionOptions.Allowlist"/> /
    /// <see cref="RecognitionOptions.Blocklist"/>) used by subsequent <see cref="Recognize(IReadOnlyList{Image{Rgb24}})"/>
    /// calls. The recognizer is shared and cached across recognition calls while these options are per-call,
    /// so the engine sets the filter immediately before each <c>Recognize</c> (or uses the
    /// <see cref="Recognize(IReadOnlyList{Image{Rgb24}}, RecognitionOptions)"/> overload, which scopes it
    /// automatically). Passing <c>null</c>, or options with empty lists, clears the filter.
    /// </summary>
    /// <param name="options">The recognition options whose allow/block lists to honor, or <c>null</c> to clear.</param>
    public void SetCharacterFilter(RecognitionOptions? options)
    {
        _allowlist = options?.Allowlist;
        _blocklist = options?.Blocklist;
    }

    /// <summary>
    /// Recognizes a batch of crops while honoring the character filter (allow/block lists) and
    /// <see cref="RecognitionOptions.BatchSize"/> carried by <paramref name="options"/>. The filter is
    /// scoped to this call: it is applied for the duration of the recognition and the previously active
    /// filter is restored afterwards, so a shared recognizer stays safe to reuse across calls with
    /// different options. A non-positive <see cref="RecognitionOptions.BatchSize"/> falls back to the
    /// constructor's batch size.
    /// </summary>
    /// <param name="crops">The upright text-line crops (caller retains ownership of each).</param>
    /// <param name="options">The recognition options whose allow/block lists and batch size to honor.</param>
    /// <returns>One (text, confidence) tuple per input crop, in the same order.</returns>
    public IReadOnlyList<(string Text, float Confidence)> Recognize(
        IReadOnlyList<Image<Rgb24>> crops, RecognitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(crops);
        ArgumentNullException.ThrowIfNull(options);

        var prevAllow = _allowlist;
        var prevBlock = _blocklist;
        _allowlist = options.Allowlist;
        _blocklist = options.Blocklist;
        try
        {
            return RecognizeCore(crops, options.BatchSize > 0 ? options.BatchSize : _batchSize);
        }
        finally
        {
            _allowlist = prevAllow;
            _blocklist = prevBlock;
        }
    }

    /// <inheritdoc />
    public (string Text, float Confidence) Recognize(Image<Rgb24> crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        return Recognize(new[] { crop })[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Text, float Confidence)> Recognize(IReadOnlyList<Image<Rgb24>> crops)
        => RecognizeCore(crops, _batchSize);

    private IReadOnlyList<(string Text, float Confidence)> RecognizeCore(
        IReadOnlyList<Image<Rgb24>> crops, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(crops);
        int count = crops.Count;
        var results = new (string Text, float Confidence)[count];
        if (count == 0) return results;

        // Sort by aspect ratio (width / height) so adjacent crops have similar normalized widths and a
        // shared batch tensor needs the least zero-padding. We sort indices, not the crops, so we can scatter
        // each result back to its original position.
        var order = new int[count];
        var ratios = new double[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
            ratios[i] = crops[i].Width / (double)Math.Max(1, crops[i].Height);
        }
        Array.Sort(ratios, order);

        // Snapshot the active character filter for the whole call so concurrent mutation can't change it
        // mid-batch. The actual mask is built lazily below, once the vocab (and thus class indices) is known.
        var allowlist = _allowlist;
        var blocklist = _blocklist;
        bool[]? selectable = null;
        bool selectableBuilt = false;

        // Process in fixed-size batches; each batch is padded to the Python-parity tensor width.
        for (int start = 0; start < count; start += batchSize)
        {
            int end = Math.Min(start + batchSize, count);
            int batchCount = end - start;

            // ratios is sorted ascending, so the batch maximum is its last element.
            int imgW = ComputeBatchTensorWidth(_imageHeight, ratios[end - 1]);

            // Resize-and-normalize each crop to min(ceil(H * w/h), imgW) — the widest crop lands on
            // the *floored* imgW, matching Python's int() truncation.
            var normalized = new float[batchCount][];
            var widths = new int[batchCount];
            for (int b = 0; b < batchCount; b++)
            {
                int srcIndex = order[start + b];
                int w = Math.Max(1, Math.Min((int)Math.Ceiling(_imageHeight * ratios[start + b]), imgW));
                normalized[b] = ResizeNormalize(crops[srcIndex], w);
                widths[b] = w;
            }

            // Build the [N, 3, H, imgW] CHW tensor, right-padding each row's width with zeros.
            var tensor = new DenseTensor<float>(new[] { batchCount, 3, _imageHeight, imgW });
            Span<float> buffer = tensor.Buffer.Span;
            int planeStride = _imageHeight * imgW;            // one channel plane per image
            int imageStride = 3 * planeStride;                // one full image
            for (int b = 0; b < batchCount; b++)
            {
                float[] src = normalized[b];
                int w = widths[b];
                int dstBase = b * imageStride;
                // src is laid out as [3, H, w]; copy each row run into the padded destination.
                for (int ch = 0; ch < 3; ch++)
                {
                    int srcChannelBase = ch * _imageHeight * w;
                    int dstChannelBase = dstBase + ch * planeStride;
                    for (int y = 0; y < _imageHeight; y++)
                    {
                        var srcRow = src.AsSpan(srcChannelBase + y * w, w);
                        srcRow.CopyTo(buffer.Slice(dstChannelBase + y * imgW, w));
                        // The remaining (imgW - w) columns stay zero — DenseTensor is zero-initialized.
                    }
                }
            }

            // Run inference and decode each output row, scattering back to the caller's order.
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var outputs = _session.Run(inputs);
            var output = outputs.First().AsTensor<float>();

            // Output shape is [N, T, C]; T and C come from the model (T depends on imgW).
            var dims = output.Dimensions;
            int timeSteps = dims.Length >= 3 ? dims[1] : 0;
            int numClasses = dims.Length >= 3 ? dims[2] : _dictLines.Count;
            // Build the vocab to match the model's class count on first use (then reuse it).
            var vocab = _vocab ??= CharacterDictionary.BuildVocab(_dictLines, numClasses);

            // Build the allow/block selectable mask once for the whole call, now that the vocab (and so the
            // class→token mapping) is known. Null when no filter is requested — the decoder then runs
            // byte-identically to the unfiltered path.
            if (!selectableBuilt)
            {
                selectable = CharacterDictionary.BuildSelectableMask(vocab, allowlist, blocklist);
                selectableBuilt = true;
            }

            // Materialize once to a flat span so the decoder can index by (row, t, c) without per-element
            // overhead from the tensor indexer.
            ReadOnlySpan<float> flat = output.ToArray();
            int rowStride = timeSteps * numClasses;

            for (int b = 0; b < batchCount; b++)
            {
                ReadOnlySpan<float> rowLogits = flat.Slice(b * rowStride, rowStride);
                results[order[start + b]] = CtcDecoder.GreedyDecode(rowLogits, timeSteps, numClasses, vocab, selectable);
            }
        }

        return results;
    }

    /// <summary>
    /// Python parity (PaddleX <c>text_recognition/processors.py:50-97</c>): the batch tensor width is
    /// <c>imgW = int(H · max_wh_ratio)</c> with <c>max_wh_ratio = max(320/H, widest crop's w/h)</c>, so
    /// every batch is at least <see cref="MinTensorWidth"/> (320) px wide, capped at
    /// <see cref="MaxTensorWidth"/> (3200) — crops beyond the cap get aspect-squeezed by the
    /// per-crop resized-width clamp.
    /// </summary>
    /// <param name="imageHeight">Recognition input height (<c>rec_image_shape</c> H, typically 48).</param>
    /// <param name="widestRatio">The widest crop's width/height ratio in the batch.</param>
    /// <returns>The batch tensor width in pixels, in [320, 3200].</returns>
    internal static int ComputeBatchTensorWidth(int imageHeight, double widestRatio)
    {
        double maxWhRatio = Math.Max(MinTensorWidth / (double)imageHeight, widestRatio);
        return Math.Min((int)(imageHeight * maxWhRatio), MaxTensorWidth);
    }

    /// <summary>
    /// Resizes <paramref name="crop"/> to <see cref="_imageHeight"/> × <paramref name="targetWidth"/>
    /// (the caller computes the width from the batch policy), then normalizes pixels to
    /// <c>(x/255 − 0.5) / 0.5</c> (i.e. [−1,1]) in a planar CHW <b>BGR</b> float array of length
    /// <c>3 · H · targetWidth</c>.
    /// </summary>
    private float[] ResizeNormalize(Image<Rgb24> crop, int targetWidth)
    {
        int h = _imageHeight;
        int w = targetWidth;

        using var resized = crop.Clone(c => c.Resize(new ResizeOptions
        {
            Size = new Size(w, h),
            Mode = ResizeMode.Stretch,
            // Python uses cv2.resize's default INTER_LINEAR.
            Sampler = KnownResamplers.Triangle,
        }));

        var data = new float[3 * h * w];
        int planeStride = h * w;
        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    Rgb24 px = row[x];
                    // (x/255 - 0.5)/0.5 == x/127.5 - 1
                    float r = px.R / 127.5f - 1f;
                    float g = px.G / 127.5f - 1f;
                    float bch = px.B / 127.5f - 1f;
                    int p = rowBase + x;
                    // The ONNX rec export consumes BGR crops (inference.yml DecodeImage img_mode: BGR).
                    data[p] = bch;                    // channel 0 (B)
                    data[planeStride + p] = g;        // channel 1 (G)
                    data[2 * planeStride + p] = r;    // channel 2 (R)
                }
            }
        });

        return data;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
