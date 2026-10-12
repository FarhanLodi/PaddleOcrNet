namespace PaddleOcrNet.Services;

/// <summary>
/// Settings for <see cref="OcrExecutionProvider.TensorRt"/>. Ignored by every other provider.
/// <para>
/// TensorRT compiles each model into an engine for the GPU it runs on, covering a range of input shapes
/// fixed in advance (an optimization profile). That is what makes it fast for OCR: the CUDA provider plans
/// its convolutions again for every new input shape, and OCR changes shape on nearly every call — the
/// recognizer's width with each batch's longest line, the detector's size with each page. The ranges below
/// bound those shapes; the engine build happens once per GPU and is cached in
/// <see cref="EngineCachePath"/>, so only the first run on a machine pays it.
/// </para>
/// </summary>
public sealed class TensorRtOptions
{
    /// <summary>
    /// Where built engines (and TensorRT's timing cache) are kept between runs. Null (the default) uses a
    /// <c>tensorrt</c> folder inside the model cache (see <see cref="PaddleOcrServiceOptions.ModelCachePath"/>).
    /// An engine is specific to the GPU, the TensorRT version and the shape ranges here; TensorRT rebuilds it
    /// when any of those change.
    /// </summary>
    public string? EngineCachePath { get; set; }

    /// <summary>
    /// Build FP16 engines. Faster, but recognition output can differ from FP32 more than the usual
    /// engine-to-engine variation does. Default <c>false</c> (FP32).
    /// </summary>
    public bool Fp16 { get; set; }

    /// <summary>
    /// The largest recognizer batch the engine accepts — the upper end of the batch range in its
    /// optimization profile. Must be at least the largest <see cref="Models.RecognitionOptions.BatchSize"/>
    /// you use. Default 16 (PaddleOCR's own default batch is 6).
    /// </summary>
    public int MaxRecognitionBatchSize { get; set; } = 16;

    /// <summary>
    /// The longest side, in pixels, the detector engine accepts. Must be at least the largest
    /// <see cref="Models.DetectionOptions.MaxSideLimit"/> you use. Default 4000, the detector's own default
    /// cap.
    /// </summary>
    public int MaxDetectionSide { get; set; } = 4000;

    /// <summary>
    /// Which of the three OCR models run on TensorRT; the rest run on CUDA. Default
    /// <see cref="TensorRtModels.All"/>. Each model on TensorRT reads a little differently from CUDA, and
    /// the detector most of all, since a slightly different box is a differently cropped line; leaving it
    /// on CUDA keeps more of CUDA's output for less of the speed.
    /// </summary>
    public TensorRtModels Models { get; set; } = TensorRtModels.All;
}

/// <summary>The OCR models <see cref="OcrExecutionProvider.TensorRt"/> can build engines for.</summary>
[Flags]
public enum TensorRtModels
{
    /// <summary>None: everything runs on CUDA.</summary>
    None = 0,

    /// <summary>The text detector.</summary>
    Detector = 1,

    /// <summary>The text-line orientation classifier.</summary>
    Classifier = 2,

    /// <summary>The recognizer.</summary>
    Recognizer = 4,

    /// <summary>All three. The default.</summary>
    All = Detector | Classifier | Recognizer,
}
