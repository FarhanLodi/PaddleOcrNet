using PaddleOcrNet.Internal;
using PaddleOcrNet.Internal.Classification;
using PaddleOcrNet.Internal.Detection;
using PaddleOcrNet.Internal.Recognition;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, no GPU, CI-safe) for <see cref="OcrExecutionProvider.TensorRt"/>:
/// the shape ranges each model's engine is built for must cover every tensor that model is fed, the engine
/// cache must land where documented, TensorRT must never be chosen by Auto, and the session-building status
/// must stay truthful whether or not TensorRT, CUDA or neither is present on the machine running the tests.
/// </summary>
public class TensorRtTests
{
    [Fact]
    public void Recognizer_profile_spans_every_batch_width_the_recognizer_can_build()
    {
        var p = TensorRtSessions.ProfileFor(TensorRtModel.Recognizer, "x", new TensorRtOptions());

        // Height is fixed; width runs from the 320-pixel floor to the 3200-pixel cap; batch from a single
        // leftover crop up to the default maximum of 16.
        Assert.Equal($"x:1x3x{SvtrRecognizer.DefaultImageHeight}x{SvtrRecognizer.MinTensorWidth}", p.Min);
        Assert.Equal($"x:{SvtrRecognizer.DefaultBatchSize}x3x48x640", p.Opt);
        Assert.Equal($"x:16x3x48x{SvtrRecognizer.MaxTensorWidth}", p.Max);
    }

    [Fact]
    public void Recognizer_batch_limit_follows_the_option_and_tunes_within_it()
    {
        var p = TensorRtSessions.ProfileFor(TensorRtModel.Recognizer, "x", new TensorRtOptions { MaxRecognitionBatchSize = 4 });

        Assert.Equal("x:4x3x48x640", p.Opt);
        Assert.Equal("x:4x3x48x3200", p.Max);
    }

    [Fact]
    public void Classifier_profile_is_its_fixed_thumbnail_in_batches_up_to_its_own_batch_size()
    {
        var p = TensorRtSessions.ProfileFor(TensorRtModel.Classifier, "x", new TensorRtOptions());

        var shape = $"{TextLineClassifier.TargetHeight}x{TextLineClassifier.TargetWidth}";
        Assert.Equal($"x:1x3x{shape}", p.Min);
        Assert.Equal($"x:{TextLineClassifier.BatchSize}x3x{shape}", p.Opt);
        Assert.Equal($"x:{TextLineClassifier.BatchSize}x3x{shape}", p.Max);
    }

    [Fact]
    public void Detector_profile_covers_one_padded_stride_up_to_the_default_side_cap()
    {
        var p = TensorRtSessions.ProfileFor(TensorRtModel.Detector, "x", new TensorRtOptions());

        // DetectionOptions.MaxSideLimit defaults to 4000, itself a multiple of the 32-pixel stride.
        Assert.Equal("x:1x3x32x32", p.Min);
        Assert.Equal("x:1x3x3296x2560", p.Opt);
        Assert.Equal("x:1x3x4000x4000", p.Max);
    }

    [Theory]
    [InlineData(1000, 1024)]   // rounded up to the stride, never down: a 1000-pixel cap can produce 1024
    [InlineData(4001, 4032)]
    [InlineData(10, 32)]       // never below one stride
    public void Detector_side_limit_is_rounded_up_to_the_stride(int limit, int expected)
    {
        var p = TensorRtSessions.ProfileFor(TensorRtModel.Detector, "x", new TensorRtOptions { MaxDetectionSide = limit });

        Assert.Equal($"x:1x3x{expected}x{expected}", p.Max);
        // The tuned-for shape never lies outside the range.
        Assert.Equal($"x:1x3x{Math.Min(3296, expected)}x{Math.Min(2560, expected)}", p.Opt);
    }

    [Fact]
    public void Profiles_use_the_models_own_input_name()
    {
        var p = TensorRtSessions.ProfileFor(TensorRtModel.Recognizer, "image", new TensorRtOptions());

        Assert.StartsWith("image:", p.Min);
        Assert.StartsWith("image:", p.Opt);
        Assert.StartsWith("image:", p.Max);
    }

    [Fact]
    public void Engine_cache_defaults_to_a_folder_in_the_model_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "paddleocrnet-trt-test-" + Guid.NewGuid().ToString("N"));
        var options = new PaddleEngineOptions { ModelCachePath = root };

        Assert.Equal(Path.Combine(ModelDownloadManager.ResolveCacheRoot(root), "tensorrt"), TensorRtSessions.EngineCachePathFor(options));
    }

    [Fact]
    public void An_explicit_engine_cache_is_used_as_an_absolute_path()
    {
        var options = new PaddleEngineOptions { TensorRt = new TensorRtOptions { EngineCachePath = "engines" } };

        Assert.Equal(Path.GetFullPath("engines"), TensorRtSessions.EngineCachePathFor(options));
    }

    [Fact]
    public void Settings_carry_the_profile_cache_and_precision()
    {
        var options = new PaddleEngineOptions
        {
            DeviceId = 1,
            TensorRt = new TensorRtOptions { EngineCachePath = "engines", Fp16 = true },
        };

        var settings = TensorRtSessions.SettingsFor(TensorRtModel.Classifier, "x", ModelHash, options);
        var profile = TensorRtSessions.ProfileFor(TensorRtModel.Classifier, "x", options.TensorRt);

        Assert.Equal("1", settings["device_id"]);
        Assert.Equal("1", settings["trt_fp16_enable"]);
        Assert.Equal("1", settings["trt_engine_cache_enable"]);
        Assert.Equal(Path.GetFullPath("engines"), settings["trt_engine_cache_path"]);
        Assert.Equal(TensorRtSessions.CachePrefix(TensorRtModel.Classifier, ModelHash, profile, fp16: true), settings["trt_engine_cache_prefix"]);
        Assert.Equal(TensorRtSessions.ProfileFor(TensorRtModel.Classifier, "x", options.TensorRt).Max, settings["trt_profile_max_shapes"]);
    }

    [Fact]
    public void Fp16_is_off_unless_asked_for()
    {
        var settings = TensorRtSessions.SettingsFor(TensorRtModel.Recognizer, "x", ModelHash, new PaddleEngineOptions());

        Assert.Equal("0", settings["trt_fp16_enable"]);
    }

    private const string ModelHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Cache_prefix_names_the_model_and_is_stable()
    {
        var profile = TensorRtSessions.ProfileFor(TensorRtModel.Recognizer, "x", new TensorRtOptions());

        var prefix = TensorRtSessions.CachePrefix(TensorRtModel.Recognizer, ModelHash, profile, fp16: false);

        Assert.Matches("^rec_[0-9a-f]{16}$", prefix);
        Assert.Equal(prefix, TensorRtSessions.CachePrefix(TensorRtModel.Recognizer, ModelHash, profile, fp16: false));
    }

    [Fact]
    public void Cache_prefix_changes_with_the_model_contents_profile_and_precision()
    {
        // ONNX Runtime's own engine name ignores the weights, so two exports with one file name and graph would
        // otherwise share an engine; a changed profile or precision must not reuse an engine built for another.
        var options = new TensorRtOptions();
        var profile = TensorRtSessions.ProfileFor(TensorRtModel.Recognizer, "x", options);
        var wider = TensorRtSessions.ProfileFor(TensorRtModel.Recognizer, "x", new TensorRtOptions { MaxRecognitionBatchSize = 32 });
        var otherModel = new string('f', 64);

        var prefixes = new[]
        {
            TensorRtSessions.CachePrefix(TensorRtModel.Recognizer, ModelHash, profile, fp16: false),
            TensorRtSessions.CachePrefix(TensorRtModel.Recognizer, otherModel, profile, fp16: false),
            TensorRtSessions.CachePrefix(TensorRtModel.Recognizer, ModelHash, wider, fp16: false),
            TensorRtSessions.CachePrefix(TensorRtModel.Recognizer, ModelHash, profile, fp16: true),
        };

        Assert.Equal(prefixes.Length, prefixes.Distinct().Count());
    }

    [Fact]
    public void Model_file_hash_follows_the_contents_not_the_name()
    {
        var dir = Path.Combine(Path.GetTempPath(), "paddleocrnet-trt-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.onnx");
            var b = Path.Combine(dir, "b.onnx");
            var c = Path.Combine(dir, "c.onnx");
            File.WriteAllBytes(a, [1, 2, 3]);
            File.WriteAllBytes(b, [1, 2, 3]);
            File.WriteAllBytes(c, [1, 2, 4]);

            Assert.Equal(TensorRtSessions.HashModelFile(a), TensorRtSessions.HashModelFile(b));
            Assert.NotEqual(TensorRtSessions.HashModelFile(a), TensorRtSessions.HashModelFile(c));
            Assert.Matches("^[0-9a-f]{64}$", TensorRtSessions.HashModelFile(a));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(4, 4)]
    [InlineData(0, 1)]   // never below one crop
    public void Recognizer_batch_limit_is_the_profiles_batch_maximum(int option, int expected)
    {
        var limits = new TensorRtOptions { MaxRecognitionBatchSize = option };

        Assert.Equal(expected, TensorRtSessions.RecognizerBatchLimit(limits));
        Assert.Equal($"x:{expected}x3x48x3200", TensorRtSessions.ProfileFor(TensorRtModel.Recognizer, "x", limits).Max);
    }

    [Theory]
    [InlineData(4000)]
    [InlineData(4001)]
    [InlineData(4010)]
    [InlineData(4020)]
    [InlineData(2048)]
    [InlineData(1000)]
    [InlineData(10)]
    public void Every_detector_input_under_the_side_cap_fits_the_profile(int maxDetectionSide)
    {
        // The detector lowers MaxSideLimit to DetectorSideLimit and then rounds each side to the nearest stride;
        // whatever the page and the per-call options, the resulting tensor must lie inside the engine's range.
        var limits = new TensorRtOptions { MaxDetectionSide = maxDetectionSide };
        int cap = TensorRtSessions.DetectorSideLimit(limits);
        var max = TensorRtSessions.ProfileFor(TensorRtModel.Detector, "x", limits).Max;
        int profileMax = int.Parse(max[(max.LastIndexOf('x') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        int[] sides = [1, 31, 33, 500, 2550, 3300, 3999, 4000, 4001, 4015, 4016, 4050, 6000, 12000];
        foreach (int w in sides)
        foreach (int h in sides)
        foreach (var (limitSideLen, limitTypeMax) in new[] { (64, false), (960, true), (5000, false), (736, false) })
        {
            var (rw, rh) = DbTextDetector.ComputeResize(w, h, limitSideLen, limitTypeMax, cap);
            Assert.InRange(rw, 32, profileMax);
            Assert.InRange(rh, 32, profileMax);
        }
    }

    [Fact]
    public void All_three_models_are_selected_by_default()
    {
        var options = new TensorRtOptions();

        Assert.True(TensorRtSessions.IsSelected(TensorRtModel.Detector, options));
        Assert.True(TensorRtSessions.IsSelected(TensorRtModel.Classifier, options));
        Assert.True(TensorRtSessions.IsSelected(TensorRtModel.Recognizer, options));
    }

    [Fact]
    public void A_model_left_out_is_not_selected()
    {
        var options = new TensorRtOptions { Models = TensorRtModels.Classifier | TensorRtModels.Recognizer };

        Assert.False(TensorRtSessions.IsSelected(TensorRtModel.Detector, options));
        Assert.True(TensorRtSessions.IsSelected(TensorRtModel.Classifier, options));
        Assert.True(TensorRtSessions.IsSelected(TensorRtModel.Recognizer, options));
    }

    [Fact]
    public void Auto_never_resolves_to_TensorRt()
    {
        // TensorRT reads a few low-confidence lines differently from CUDA, so it is opt-in only.
        Assert.NotEqual(OcrExecutionProvider.TensorRt, ExecutionProviderResolver.Resolve(OcrExecutionProvider.Auto, logger: null));
    }

    [Fact]
    public void An_explicit_TensorRt_request_is_passed_through()
    {
        Assert.Equal(OcrExecutionProvider.TensorRt, ExecutionProviderResolver.Resolve(OcrExecutionProvider.TensorRt, logger: null));
    }

    [Fact]
    public void Service_options_carry_the_TensorRt_settings_to_the_engine()
    {
        var service = new PaddleOcrServiceOptions
        {
            ExecutionProvider = OcrExecutionProvider.TensorRt,
            TensorRt = new TensorRtOptions { Fp16 = true, MaxRecognitionBatchSize = 8, MaxDetectionSide = 2048, EngineCachePath = "e" },
        };

        var engine = service.ToEngineOptions();

        Assert.Equal(OcrExecutionProvider.TensorRt, engine.ExecutionProvider);
        Assert.True(engine.TensorRt.Fp16);
        Assert.Equal(8, engine.TensorRt.MaxRecognitionBatchSize);
        Assert.Equal(2048, engine.TensorRt.MaxDetectionSide);
        Assert.Equal("e", engine.TensorRt.EngineCachePath);
    }

    [Fact]
    public void UseGpu_does_not_override_an_explicit_TensorRt_request()
    {
        var engine = new PaddleOcrServiceOptions { ExecutionProvider = OcrExecutionProvider.TensorRt, UseGpu = true }.ToEngineOptions();

        Assert.Equal(OcrExecutionProvider.TensorRt, engine.ExecutionProvider);
    }

    [Fact]
    public void TensorRt_request_does_not_throw_and_reports_a_truthful_provider()
    {
        var result = ExecutionProviderResolver.BuildSessionOptionsWithStatus(
            OcrExecutionProvider.TensorRt, new PaddleEngineOptions(), logger: null);

        Assert.NotNull(result.Options);
        switch (result.ActiveProvider)
        {
            case OcrExecutionProvider.TensorRt:
                Assert.Null(result.ProviderFailureHint);
                break;
            case OcrExecutionProvider.Cuda:
                // CUDA attached but TensorRT did not: OCR stays on the GPU, and says why it is not on TensorRT.
                Assert.Contains("TensorRT", result.ProviderFailureHint);
                break;
            default:
                // No CUDA at all, so no TensorRT either.
                Assert.Equal(OcrExecutionProvider.Cpu, result.ActiveProvider);
                Assert.False(string.IsNullOrWhiteSpace(result.ProviderFailureHint));
                break;
        }

        result.Options.Dispose();
    }
}
