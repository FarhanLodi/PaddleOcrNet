using System.Text.Json;
using PaddleOcrNet.Tests.Evaluation;
using Xunit;

namespace PaddleOcrNet.Tests;

public class ConfidenceCalibrationCommandTests
{
    [SkippableFact]
    public void FitAndEvaluateLabeledCalibrationManifest()
    {
        string? input = Environment.GetEnvironmentVariable("PADDLEOCRNET_CALIBRATION_INPUT");
        Skip.If(string.IsNullOrWhiteSpace(input), "Set PADDLEOCRNET_CALIBRATION_INPUT to a labeled calibration/test manifest.");
        var options = ManifestJsonOptions();
        var manifest = JsonSerializer.Deserialize<CalibrationManifest>(File.ReadAllText(input!), options)
            ?? throw new InvalidDataException("Calibration manifest is null.");
        if (manifest.Samples.Any(s => s.Split is not ("calibration" or "test")))
            throw new InvalidDataException("Command accepts only calibration and test rows.");
        var fit = ConfidenceCalibration.Fit(manifest.Samples.Where(s => s.Split == "calibration").ToArray(), manifest.Assets, manifest.Version);
        var heldOut = manifest.Samples.Where(s => s.Split == "test").ToArray();
        var overall = ConfidenceCalibration.Evaluate(fit, heldOut);
        var byPack = heldOut.GroupBy(s => s.Pack).ToDictionary(g => g.Key, g => ConfidenceCalibration.Evaluate(fit, g.ToArray()));
        string output = Environment.GetEnvironmentVariable("PADDLEOCRNET_CALIBRATION_OUTPUT")
            ?? Path.Combine(OcrDatasetBenchmarkTests.RepoRoot, "artifacts", "calibration");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "curves.json"), JsonSerializer.Serialize(fit.Curves, options));
        File.WriteAllText(Path.Combine(output, "evaluation.json"), JsonSerializer.Serialize(new { overall, byPack, fit.FittingDocuments }, options));
        Assert.Equal(heldOut.Length, overall.Samples);
    }

    [Theory]
    [InlineData("{\"documentId\":\"doc\",\"pack\":\"pack\",\"split\":\"calibration\",\"rawConfidence\":0.8}")]
    [InlineData("{\"documentId\":\"doc\",\"pack\":\"pack\",\"split\":\"calibration\",\"correct\":false}")]
    public void Manifest_rejects_missing_ground_truth_or_raw_score(string sample)
    {
        // Exercise the command's serializer directly: neither missing label nor missing score
        // may silently become false/zero through positional-record constructor defaults.
        string json = "{\"version\":\"v1\",\"assets\":{},\"samples\":[" + sample + "]}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CalibrationManifest>(json, ManifestJsonOptions()));
    }

    [Fact]
    public void Manifest_accepts_explicit_false_label_and_zero_score()
    {
        const string json = "{\"version\":\"v1\",\"assets\":{},\"samples\":[{\"documentId\":\"doc\",\"pack\":\"pack\",\"split\":\"calibration\",\"rawConfidence\":0,\"correct\":false}]}";
        var manifest = JsonSerializer.Deserialize<CalibrationManifest>(json, ManifestJsonOptions());
        var sample = Assert.Single(Assert.IsType<CalibrationManifest>(manifest).Samples);
        Assert.False(sample.Correct);
        Assert.Equal(0, sample.RawConfidence);
    }

    private static JsonSerializerOptions ManifestJsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true
    };

    private sealed record CalibrationManifest(string Version, Dictionary<string, CalibrationAssets> Assets, CalibrationSample[] Samples);
}
