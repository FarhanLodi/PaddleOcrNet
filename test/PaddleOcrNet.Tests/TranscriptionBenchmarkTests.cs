using System.Text.Json;
using PaddleOcrNet.Tests.Evaluation;
using Xunit;

namespace PaddleOcrNet.Tests;

public sealed class TranscriptionBenchmarkTests
{
    [Theory]
    [InlineData("😀A", "😀B", 1, 2)]
    [InlineData("abc", "ac", 1, 3)]
    [InlineData("a", "abcd", 3, 1)]
    [InlineData("", "😀", 1, 0)]
    [InlineData("", "", 0, 0)]
    public void Character_score_uses_scalars_and_counts_all_edits(string reference, string actual, int edits, int length)
    {
        var score = TranscriptionScorer.Score(reference, actual);
        Assert.Equal(edits, score.CharacterEdits);
        Assert.Equal(length, score.ReferenceCharacters);
        Assert.Equal(reference == actual, score.ExactMatch);
        if (length == 0) Assert.Null(score.Cer);
    }

    [Fact]
    public void Normalization_is_explicit_and_preserves_meaningful_symbols()
    {
        const string expected = "café -8.47\nID: AB";
        const string actual = "cafe\u0301  -8.47\r\nID:  AB";
        Assert.False(TranscriptionScorer.Score(expected, actual).ExactMatch);
        Assert.True(TranscriptionScorer.Score(TranscriptionScorer.Normalize(expected), TranscriptionScorer.Normalize(actual)).ExactMatch);
        Assert.NotEqual(TranscriptionScorer.Normalize("-8.47"), TranscriptionScorer.Normalize("847"));
        Assert.NotEqual(TranscriptionScorer.Normalize("Ａ"), TranscriptionScorer.Normalize("A"));
    }

    [Fact]
    public void Word_and_line_scores_detect_omissions()
    {
        var score = TranscriptionScorer.Score("one two\nthree", "one\nthree");
        Assert.Equal(1, score.WordEdits);
        Assert.Equal(3, score.ReferenceWords);
        Assert.Equal(1, score.LineEdits);
        Assert.Equal(1, score.ExactLines);
        Assert.Equal(0.5, score.ExactLineRecall);
    }

    [Fact]
    public void Slices_separate_synthetic_real_and_blank_false_positives()
    {
        var manifest = Manifest(
            Page("a", "abc"), Page("blank", ""), Page("real", "xyz") with { SourceKind = "real" });
        var report = TranscriptionBenchmark.Evaluate(manifest, Run(
            Prediction("a", "axc"), Prediction("blank", "invented"), Prediction("real", "xyz")));
        Assert.Equal(2, report.Slices.Length);
        var synthetic = report.Slices.Single(s => s.SourceKind == "synthetic");
        Assert.Equal(1d / 3, synthetic.RawCer);
        Assert.Equal(1, synthetic.BlankPagesWithFalsePositives);
        Assert.Equal(8, synthetic.FalsePositiveCharacters);
        Assert.Empty(report.Provenance.Predictions);
    }

    [Fact]
    public void Sensitive_values_require_exact_match_in_the_labeled_field()
    {
        var page = Page("a", "8:47") with { SensitiveTokens = new() { ["time"] = "8:47", ["amount"] = "-1.00" } };
        var prediction = Prediction("a", "8147") with { SensitiveTokens = new() { ["time"] = "8147", ["amount"] = "-1.00" } };
        var score = Assert.Single(TranscriptionBenchmark.Evaluate(Manifest(page), Run(prediction)).Pages);
        Assert.Equal(1, score.SensitiveTokensCorrect);
        Assert.Equal(2, score.SensitiveTokensTotal);
    }

    [Fact]
    public void Missing_predictions_empty_corpora_and_split_leakage_fail()
    {
        Assert.Throws<InvalidDataException>(() => TranscriptionBenchmark.Evaluate(Manifest(), Run()));
        Assert.Throws<InvalidDataException>(() => TranscriptionBenchmark.Evaluate(Manifest(Page("a", "a")), Run()));
        var pages = new[] { Page("a", "a"), Page("b", "b") with { OriginGroup = "a", Split = "calibration" } };
        Assert.Throws<InvalidDataException>(() => TranscriptionBenchmark.Evaluate(Manifest(pages), Run(Prediction("a", "a"), Prediction("b", "b"))));
    }

    [SkippableFact]
    public async Task Evaluate_supplied_full_transcription_manifest()
    {
        string? manifestPath = Environment.GetEnvironmentVariable("PADDLEOCRNET_BENCHMARK_MANIFEST");
        Skip.If(string.IsNullOrEmpty(manifestPath), "Set PADDLEOCRNET_BENCHMARK_MANIFEST to evaluate an annotated corpus.");
        string predictionPath = Environment.GetEnvironmentVariable("PADDLEOCRNET_BENCHMARK_PREDICTIONS")
            ?? throw new InvalidDataException("PADDLEOCRNET_BENCHMARK_PREDICTIONS is required.");
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        var manifest = JsonSerializer.Deserialize<BenchmarkManifest>(await File.ReadAllTextAsync(manifestPath!), json)!;
        var run = JsonSerializer.Deserialize<BenchmarkRun>(await File.ReadAllTextAsync(predictionPath), json)!;
        var report = TranscriptionBenchmark.Evaluate(manifest, run);
        string reportPath = Environment.GetEnvironmentVariable("PADDLEOCRNET_BENCHMARK_REPORT")
            ?? Path.Combine(Path.GetTempPath(), "paddleocr-transcription-report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, json));
        Assert.NotEmpty(report.Pages);
    }

    private static BenchmarkPage Page(string id, string text) => new(id, id, id, "synthetic", "heldout", "text", text, new());
    private static BenchmarkPrediction Prediction(string id, string text) => new(id, text, new());
    private static BenchmarkManifest Manifest(params BenchmarkPage[] pages) => new("unit-fixture-v1", "test-only", pages);
    private static BenchmarkRun Run(params BenchmarkPrediction[] predictions) => new("test-revision", "test-runtime", "CPU", "FP32",
        "test-machine", 1, "{}", new() { ["test-model"] = new string('a', 64) }, predictions);
}
