using PaddleOcrNet.Tests.Evaluation;
using Xunit;

namespace PaddleOcrNet.Tests;

public class ConfidenceCalibrationTests
{
    private static readonly Dictionary<string, CalibrationAssets> Assets = new()
    { ["en"] = new(new string('a', 64), new string('b', 64)) };
    private static CalibrationSample Row(string id, double score, bool correct, string split = "calibration")
        => new(id, "en", split, score, correct);

    [Fact]
    public void IsotonicFitPoolsViolationsWithCountsAndTiedScores()
    {
        var fit = ConfidenceCalibration.Fit([
            Row("a", .2, true), Row("b", .5, false), Row("c", .5, false), Row("d", .9, true)], Assets, "v1");
        var curve = fit.Curves["en"];
        Assert.Equal(new[] { 0d, .2, .5, .9, 1 }, curve.RawConfidence);
        Assert.Equal(new[] { 1d / 3, 1d / 3, 1d / 3, 1, 1 }, curve.Correctness);
        curve.Validate(Assets["en"].ModelSha256, Assets["en"].DictionarySha256);
    }

    [Fact]
    public void SingleConfidenceProducesUsableEndpointCurve()
    {
        var fit = ConfidenceCalibration.Fit([Row("a", .5, false), Row("b", .5, true)], Assets, "v1");
        Assert.Equal(.5, fit.Curves["en"].Map(0));
        Assert.Equal(.5, fit.Curves["en"].Map(1));
    }

    [Fact]
    public void HeldOutLabelsCannotEnterFitOrLeakByDocument()
    {
        Assert.Throws<ArgumentException>(() => ConfidenceCalibration.Fit([Row("a", .5, true, "test")], Assets, "v1"));
        var fit = ConfidenceCalibration.Fit([Row("a", .5, true)], Assets, "v1");
        Assert.Throws<ArgumentException>(() => ConfidenceCalibration.Evaluate(fit, [Row("a", .5, true, "test")]));
    }

    [Fact]
    public void EvaluationComputesBrierEceAndWholeTieRiskCoverage()
    {
        var fit = ConfidenceCalibration.Fit([Row("a", .5, false), Row("b", .5, true)], Assets, "v1");
        var evaluation = ConfidenceCalibration.Evaluate(fit, [Row("c", .3, true, "test"), Row("d", .9, false, "test")]);
        Assert.Equal(0, evaluation.ExpectedCalibrationError);
        Assert.Equal(.25, evaluation.BrierScore);
        var point = Assert.Single(evaluation.RiskCoverage);
        Assert.Equal(1, point.Coverage);
        Assert.Equal(.5, point.ErrorRate);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.1)]
    [InlineData(1.1)]
    public void InvalidScoresAreRejected(double score)
        => Assert.Throws<ArgumentException>(() => ConfidenceCalibration.Fit([Row("a", score, true)], Assets, "v1"));

    [Fact]
    public void EmptyLabelsAndMissingHashesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ConfidenceCalibration.Fit([], Assets, "v1"));
        Assert.Throws<ArgumentException>(() => ConfidenceCalibration.Fit([Row("a", .2, false)], new Dictionary<string, CalibrationAssets>(), "v1"));
    }
}
