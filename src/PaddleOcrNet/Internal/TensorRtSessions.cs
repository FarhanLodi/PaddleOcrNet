using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PaddleOcrNet.Internal.Classification;
using PaddleOcrNet.Internal.Recognition;
using PaddleOcrNet.Services;

namespace PaddleOcrNet.Internal;

/// <summary>The OCR models that get a TensorRT engine of their own.</summary>
internal enum TensorRtModel
{
    /// <summary>The DB text detector.</summary>
    Detector,

    /// <summary>The text-line orientation classifier.</summary>
    Classifier,

    /// <summary>The SVTR recognizer, whichever pack.</summary>
    Recognizer,
}

/// <summary>
/// One input shape range for a TensorRT optimization profile, in ONNX Runtime's
/// <c>name:NxCxHxW</c> form. The engine accepts any shape between <see cref="Min"/> and <see cref="Max"/>
/// and is tuned for <see cref="Opt"/>.
/// </summary>
internal readonly record struct TensorRtProfile(string Min, string Opt, string Max);

/// <summary>
/// Session options for <see cref="OcrExecutionProvider.TensorRt"/>, built per model.
/// <para>
/// Per model because an optimization profile is keyed by input name and PaddleOCR's exported detector,
/// classifier and recognizer all call their input <c>x</c>: one set of options shared by all three, as the
/// other providers use, could carry only one profile, and no single range fits a 4000-pixel page, an
/// 80×160 line thumbnail and a 48-pixel-high line. The ranges come from the code that builds each model's
/// tensor, so they cover every shape the engine can be fed.
/// </para>
/// </summary>
internal static class TensorRtSessions
{
    /// <summary>The detector's resize rounds both sides to a multiple of this (DBNet's stride).</summary>
    private const int DetectorStride = 32;

    /// <summary>
    /// The page shape the detector engine is tuned for: US Letter at 300 dpi, rounded to the stride — the
    /// common case for scanned documents. Any size in range runs; this only steers TensorRT's kernel choice.
    /// </summary>
    private const int OptimalDetectorHeight = 3296, OptimalDetectorWidth = 2560;

    /// <summary>A recognizer batch width tuned for: a typical line, a little over twice the minimum.</summary>
    private const int OptimalRecognizerWidth = 640;

    /// <summary>
    /// The shape range for <paramref name="model"/>, given its input name and the caller's limits.
    /// </summary>
    internal static TensorRtProfile ProfileFor(TensorRtModel model, string input, TensorRtOptions limits)
    {
        switch (model)
        {
            case TensorRtModel.Detector:
            {
                // DbTextDetector feeds one image at a time, each side rounded to the stride and the long
                // side capped at DetectionOptions.MaxSideLimit; tiny images are padded up to one stride.
                int max = RoundUp(Math.Max(DetectorStride, limits.MaxDetectionSide), DetectorStride);
                int optH = Math.Min(OptimalDetectorHeight, max), optW = Math.Min(OptimalDetectorWidth, max);
                return new(
                    Shape(input, 1, 3, DetectorStride, DetectorStride),
                    Shape(input, 1, 3, optH, optW),
                    Shape(input, 1, 3, max, max));
            }

            case TensorRtModel.Classifier:
                // TextLineClassifier: fixed 80×160 thumbnails, in batches of up to its own batch size.
                return new(
                    Shape(input, 1, 3, TextLineClassifier.TargetHeight, TextLineClassifier.TargetWidth),
                    Shape(input, TextLineClassifier.BatchSize, 3, TextLineClassifier.TargetHeight, TextLineClassifier.TargetWidth),
                    Shape(input, TextLineClassifier.BatchSize, 3, TextLineClassifier.TargetHeight, TextLineClassifier.TargetWidth));

            default:
            {
                // SvtrRecognizer: height 48, width from 320 to 3200 depending on the batch's widest line,
                // batch up to RecognitionOptions.BatchSize (the last batch of a page is smaller).
                int maxBatch = Math.Max(1, limits.MaxRecognitionBatchSize);
                int optBatch = Math.Min(SvtrRecognizer.DefaultBatchSize, maxBatch);
                const int h = SvtrRecognizer.DefaultImageHeight;
                return new(
                    Shape(input, 1, 3, h, SvtrRecognizer.MinTensorWidth),
                    Shape(input, optBatch, 3, h, OptimalRecognizerWidth),
                    Shape(input, maxBatch, 3, h, SvtrRecognizer.MaxTensorWidth));
            }
        }
    }

    /// <summary>Whether <paramref name="model"/> is one of <see cref="TensorRtOptions.Models"/>.</summary>
    internal static bool IsSelected(TensorRtModel model, TensorRtOptions options) => model switch
    {
        TensorRtModel.Detector => options.Models.HasFlag(TensorRtModels.Detector),
        TensorRtModel.Classifier => options.Models.HasFlag(TensorRtModels.Classifier),
        _ => options.Models.HasFlag(TensorRtModels.Recognizer),
    };

    /// <summary>
    /// Where engines and the timing cache are kept: <see cref="TensorRtOptions.EngineCachePath"/>, or a
    /// <c>tensorrt</c> folder in the model cache.
    /// </summary>
    internal static string EngineCachePathFor(PaddleEngineOptions options) =>
        string.IsNullOrWhiteSpace(options.TensorRt.EngineCachePath)
            ? Path.Combine(ModelDownloadManager.ResolveCacheRoot(options.ModelCachePath), "tensorrt")
            : Path.GetFullPath(options.TensorRt.EngineCachePath);

    /// <summary>The provider settings for one model, as ONNX Runtime's TensorRT provider takes them.</summary>
    internal static Dictionary<string, string> SettingsFor(TensorRtModel model, string input, PaddleEngineOptions options)
    {
        var profile = ProfileFor(model, input, options.TensorRt);
        var cache = EngineCachePathFor(options);
        return new Dictionary<string, string>
        {
            ["device_id"] = options.DeviceId.ToString(CultureInfo.InvariantCulture),
            ["trt_fp16_enable"] = options.TensorRt.Fp16 ? "1" : "0",
            ["trt_engine_cache_enable"] = "1",
            ["trt_engine_cache_path"] = cache,
            // Engines are named from a hash of the model, so packs never collide; the prefix only makes
            // the cache folder readable.
            ["trt_engine_cache_prefix"] = PrefixOf(model),
            ["trt_timing_cache_enable"] = "1",
            ["trt_timing_cache_path"] = cache,
            ["trt_profile_min_shapes"] = profile.Min,
            ["trt_profile_opt_shapes"] = profile.Opt,
            ["trt_profile_max_shapes"] = profile.Max,
        };
    }

    /// <summary>
    /// Opens <paramref name="modelPath"/> on TensorRT, with CUDA behind it, and builds its engine (or loads
    /// it from the cache) by running it once at the profile's optimal shape. Building at load rather than on
    /// the first real call means a model TensorRT cannot handle is found here, where the caller can fall back,
    /// instead of failing an OCR request.
    /// </summary>
    internal static InferenceSession Open(TensorRtModel model, string modelPath, PaddleEngineOptions options, ILogger? logger)
    {
        var input = InputNameOf(modelPath);
        Directory.CreateDirectory(EngineCachePathFor(options));

        using var so = ExecutionProviderResolver.CreateBaseOptions(options);
        using (var trt = new OrtTensorRTProviderOptions())
        {
            trt.UpdateOptions(SettingsFor(model, input, options));
            so.AppendExecutionProvider_Tensorrt(trt);
        }
        so.AppendExecutionProvider_CUDA(options.DeviceId);

        var session = new InferenceSession(modelPath, so);
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var dims = ParseShape(ProfileFor(model, input, options.TensorRt).Opt);
            var tensor = new DenseTensor<float>(dims);
            using (session.Run([NamedOnnxValue.CreateFromTensor(input, tensor)])) { }
            logger?.LogInformation(
                "TensorRT engine for the {Model} ready in {Seconds:F1} s (cache: {Cache}).",
                model, stopwatch.Elapsed.TotalSeconds, EngineCachePathFor(options));
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Null when ONNX Runtime's TensorRT provider can be attached here; otherwise why not. Attaching loads
    /// the provider library and, through it, TensorRT's own, so a missing or mismatched TensorRT shows up
    /// here, at engine start-up, rather than at the first model load.
    /// </summary>
    internal static string? AvailabilityHint(PaddleEngineOptions options)
    {
        try
        {
            using var probe = new SessionOptions();
            probe.AppendExecutionProvider_Tensorrt(options.DeviceId);
            return null;
        }
        catch (Exception ex)
        {
            return "TensorRT execution provider unavailable; OCR will run on CUDA. It needs TensorRT 10 built for " +
                   "the same CUDA major as the loaded ONNX Runtime, with its libraries (nvinfer_10, nvonnxparser_10) " +
                   $"on PATH. ({ex.Message})";
        }
    }

    /// <summary>
    /// The model's input name, read from the model itself rather than assumed, so a custom recognizer
    /// exported with another name still gets a profile that applies to it.
    /// </summary>
    private static string InputNameOf(string modelPath)
    {
        using var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
        using var session = new InferenceSession(modelPath, so);
        return session.InputMetadata.Keys.First();
    }

    private static string PrefixOf(TensorRtModel model) => model switch
    {
        TensorRtModel.Detector => "det",
        TensorRtModel.Classifier => "cls",
        _ => "rec",
    };

    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;

    private static string Shape(string input, int n, int c, int h, int w) =>
        string.Create(CultureInfo.InvariantCulture, $"{input}:{n}x{c}x{h}x{w}");

    private static int[] ParseShape(string shape) =>
        shape[(shape.LastIndexOf(':') + 1)..].Split('x').Select(d => int.Parse(d, CultureInfo.InvariantCulture)).ToArray();
}
