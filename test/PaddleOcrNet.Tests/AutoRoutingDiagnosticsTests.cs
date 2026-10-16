using System.Diagnostics;
using PaddleOcrNet.Diagnostics;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

[Trait("Category", "Integration")]
public sealed class AutoRoutingDiagnosticsTests
{
    [SkippableFact]
    public async Task Calibrated_bounded_route_preserves_raw_scores_and_source_coordinates()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("PADDLEOCRNET_RUN_INTEGRATION") == "1", "Enable model integration.");
        var pack = PaddleModelRegistry.English;
        var calibration = new RecognitionCalibration
        {
            Version = "identity-test-only-not-trained", ModelSha256 = pack.Model.Sha256!, DictionarySha256 = pack.Dictionary.Sha256!,
            RawConfidence = new double[] { 0, 1 }, Correctness = new double[] { 0, 1 },
        };
        using var service = new PaddleOcrService(new PaddleOcrServiceOptions { ExecutionProvider = OcrExecutionProvider.Cpu, IntraOpNumThreads = 2 });
        string path = Path.Combine(OcrDatasetBenchmarkTests.RepoRoot, "test", "Assets", "ocr_test1.png");
        var options = RecognitionOptions.Default with
        {
            AutoDetectCandidates = new[] { "en", "ar" },
            AutoRouting = new AutoRoutingOptions { MaxCandidatePacks = 1,
                Calibration = new Dictionary<string, RecognitionCalibration> { [pack.Name] = calibration } },
        };
        var bounded = await service.ExtractTextFromImage(path, new[] { OcrLanguage.Auto }, options);
        var direct = await service.ExtractTextFromImage(path, new[] { OcrLanguage.English });
        Assert.Equal(direct.Lines.Select(l => (l.Text, l.Confidence)), bounded.Lines.Select(l => (l.Text, l.Confidence)));
        Assert.Equal(OcrCoordinateFrame.SourcePixels, bounded.Coordinates!.Frame);
        Assert.True(bounded.Coordinates.SourceMappingAvailable);
        Assert.False(bounded.Coordinates.NonlinearDewarpApplied);
    }

    [SkippableFact]
    public async Task Auto_routing_reports_decision_counts_without_document_text()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("PADDLEOCRNET_RUN_INTEGRATION") == "1",
            "Set PADDLEOCRNET_RUN_INTEGRATION=1 to run model-backed routing diagnostics.");
        var stopped = new List<Activity>();
        using var parent = new Activity("routing_diagnostics_test").Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PaddleOcrDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "paddleocr.auto_routing" && activity.RootId == parent.RootId)
                    lock (stopped) stopped.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var service = new PaddleOcrService(new PaddleOcrServiceOptions
        {
            ExecutionProvider = OcrExecutionProvider.Cpu,
            IntraOpNumThreads = 2,
        });
        var result = await service.ExtractTextFromImage(
            Path.Combine(OcrDatasetBenchmarkTests.RepoRoot, "test", "Assets", "ocr_test1.png"),
            new[] { OcrLanguage.Auto },
            RecognitionOptions.Default with { AutoDetectCandidates = new[] { "en" } });
        Assert.NotEmpty(result.Lines);
        var trace = Assert.Single(stopped);
        Assert.Equal(1, trace.GetTagItem("ocr.candidate_count"));
        Assert.Equal(1, trace.GetTagItem("ocr.evaluated_count"));
        Assert.Equal(false, trace.GetTagItem("ocr.confidence_calibrated"));
        Assert.Equal("raw_confidence_per_crop_sweep", trace.GetTagItem("ocr.selection_reason"));
        var allowed = new[] { "ocr.candidate_count", "ocr.crop_count", "ocr.confidence_calibrated",
            "ocr.evaluated_count", "ocr.selection_reason", "ocr.winning_pack_count" };
        Assert.All(trace.TagObjects, tag => Assert.Contains(tag.Key, allowed));
    }
}
