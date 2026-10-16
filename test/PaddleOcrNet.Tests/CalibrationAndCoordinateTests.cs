using System.Text.Json;
using PaddleOcrNet.Models;
using PaddleOcrNet.Internal;
using Xunit;

namespace PaddleOcrNet.Tests;

public sealed class CalibrationAndCoordinateTests
{
    [Fact]
    public void Calibration_is_bound_to_actual_variant_and_rejects_local_override()
    {
        var mobile = PaddleModelRegistry.MobileRecognizer;
        var actual = AutoRoutingPolicy.ResolveActualCandidates(new[] { mobile, PaddleModelRegistry.ChineseTraditional }, OcrModelVariant.Server);
        Assert.Equal(PaddleModelRegistry.ServerRecognizer, Assert.Single(actual));
        var mobileCurve = Curve() with { ModelSha256 = mobile.Model.Sha256!, DictionarySha256 = mobile.Dictionary.Sha256! };
        Assert.Throws<ArgumentException>(() => AutoRoutingPolicy.ValidateCalibration(actual,
            new Dictionary<string, RecognitionCalibration> { [actual[0].Name] = mobileCurve }, false));
        Assert.Throws<ArgumentException>(() => AutoRoutingPolicy.ValidateCalibration(new[] { mobile },
            new Dictionary<string, RecognitionCalibration> { [mobile.Name] = mobileCurve }, true));
        AutoRoutingPolicy.ValidateCalibration(new[] { mobile },
            new Dictionary<string, RecognitionCalibration> { [mobile.Name] = mobileCurve }, false);
    }

    private static RecognitionCalibration Curve() => new()
    {
        Version = "calibration-only-v1", ModelSha256 = new string('A', 64), DictionarySha256 = new string('B', 64),
        RawConfidence = new double[] { 0, .5, 1 }, Correctness = new double[] { .1, .3, .9 },
    };

    [Fact]
    public void Calibration_interpolates_correctness_and_rejects_incompatible_assets()
    {
        var curve = Curve();
        curve.Validate(new string('A', 64), new string('B', 64));
        Assert.Equal(.6, curve.Map(.75), 10);
        Assert.Equal(.1, curve.Map(-1));
        Assert.Equal(.9, curve.Map(2));
        Assert.Equal(0, curve.Map(double.NaN));
        Assert.Throws<ArgumentException>(() => curve.Validate(new string('C', 64), new string('B', 64)));
        Assert.Throws<ArgumentException>(() => (curve with { Correctness = new double[] { 0, .9, .2 } })
            .Validate(curve.ModelSha256, curve.DictionarySha256));
        Assert.Throws<ArgumentException>(() => (curve with { RawConfidence = new double[] { 0, 0, 1 } })
            .Validate(curve.ModelSha256, curve.DictionarySha256));
    }

    [Fact]
    public void Generated_json_preserves_noninvertible_frame_and_raw_result()
    {
        var result = OcrResult.Empty with { FullText = "8:47", Coordinates = new()
        {
            Frame = OcrCoordinateFrame.DewarpedPixels, SourceMappingAvailable = false,
            NonlinearDewarpApplied = true, DocumentRotationDegrees = 90,
        } };
        string json = JsonSerializer.Serialize(result, PaddleOcrJsonContext.Default.OcrResult);
        var roundtrip = JsonSerializer.Deserialize(json, PaddleOcrJsonContext.Default.OcrResult)!;
        Assert.Equal(result.Coordinates, roundtrip.Coordinates);
        Assert.Equal("8:47", roundtrip.FullText);
        Assert.False(roundtrip.Coordinates!.SourceMappingAvailable);
    }
}
