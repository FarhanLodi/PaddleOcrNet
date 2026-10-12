using PaddleOcrNet.Internal;
using PaddleOcrNet.Internal.Classification;
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

        var settings = TensorRtSessions.SettingsFor(TensorRtModel.Classifier, "x", options);

        Assert.Equal("1", settings["device_id"]);
        Assert.Equal("1", settings["trt_fp16_enable"]);
        Assert.Equal("1", settings["trt_engine_cache_enable"]);
        Assert.Equal(Path.GetFullPath("engines"), settings["trt_engine_cache_path"]);
        Assert.Equal("cls", settings["trt_engine_cache_prefix"]);
        Assert.Equal(TensorRtSessions.ProfileFor(TensorRtModel.Classifier, "x", options.TensorRt).Max, settings["trt_profile_max_shapes"]);
    }

    [Fact]
    public void Fp16_is_off_unless_asked_for()
    {
        var settings = TensorRtSessions.SettingsFor(TensorRtModel.Recognizer, "x", new PaddleEngineOptions());

        Assert.Equal("0", settings["trt_fp16_enable"]);
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
