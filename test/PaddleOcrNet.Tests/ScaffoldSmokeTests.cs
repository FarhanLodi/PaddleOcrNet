using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Compile-time smoke tests over the scaffolded contracts. These assert only the wiring that already
/// exists (options defaults, registry shape, reading-order helper) — not the stubbed OCR pipeline.
/// Downstream agents add real behavioural tests as each module body is filled in.
/// </summary>
public class ScaffoldSmokeTests
{
    [Fact]
    public void DetectionOptions_Defaults_MatchPaddle()
    {
        var d = DetectionOptions.Default;
        Assert.Equal(960, d.LimitSideLen);
        Assert.Equal(0.3, d.DetThreshold);
        Assert.Equal(0.6, d.BoxThreshold);
        Assert.Equal(1.5, d.UnclipRatio);
    }

    [Fact]
    public void RecognitionOptions_Defaults_MatchPaddle()
    {
        var r = RecognitionOptions.Default;
        Assert.Equal(0.5, r.DropScore);
        Assert.Equal(TextGrouping.Line, r.Grouping);
    }

    [Fact]
    public void ServiceOptions_MapToEngineOptions()
    {
        var opts = new PaddleOcrServiceOptions { UseTextLineOrientation = true };
        // ToEngineOptions is internal; reachable via InternalsVisibleTo.
        var engine = opts.ToEngineOptions();
        Assert.True(engine.UseTextLineOrientation);
    }

    [Fact]
    public void Registry_ResolvesEnglish()
    {
        var pack = PaddleModelRegistry.FindByLanguage("en");
        Assert.NotNull(pack);
        Assert.Contains("en", pack!.Languages);
    }
}
