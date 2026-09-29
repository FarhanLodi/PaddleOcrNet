using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Real-pipeline check for <see cref="PreprocessingOptions.Deskew"/> (issue #9): a slightly skewed page read
/// with deskew on must return its line and word boxes where the same page read with deskew off puts them —
/// on the ink in the caller's image, not offset by the enlarged rotation canvas. Gated like
/// <see cref="AssetsOcrTests"/> behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeskewIntegrationTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";

    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private readonly ITestOutputHelper _out;

    public DeskewIntegrationTests(ITestOutputHelper output) => _out = output;

    private static string AssetsDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaddleOcrNet.sln")))
                dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repo root not found"), "test", "Assets");
        }
    }

    private static RecognitionOptions Opts(bool deskew) => new()
    {
        Grouping = TextGrouping.Line,
        ReturnWordBoxes = true,
        UseDocOrientation = false,
        Preprocessing = new PreprocessingOptions { Deskew = deskew, Denoise = true },
    };

    /// <summary>Median of the per-item centre shifts (on minus off) over texts that occur once in both readings.</summary>
    private static (double Dx, double Dy, int Matched) MedianShift(
        IEnumerable<(string Text, OcrBoundingBox Box)> on, IEnumerable<(string Text, OcrBoundingBox Box)> off)
    {
        static Dictionary<string, OcrBoundingBox> Unique(IEnumerable<(string Text, OcrBoundingBox Box)> items) =>
            items.Where(i => i.Text.Length >= 4)
                .GroupBy(i => i.Text)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single().Box);

        var a = Unique(on);
        var b = Unique(off);
        var dx = new List<double>();
        var dy = new List<double>();
        foreach (var (text, box) in a)
        {
            if (!b.TryGetValue(text, out var other)) continue;
            dx.Add(box.CenterX - other.CenterX);
            dy.Add(box.CenterY - other.CenterY);
        }
        if (dx.Count == 0) return (double.NaN, double.NaN, 0);
        dx.Sort();
        dy.Sort();
        return (dx[dx.Count / 2], dy[dy.Count / 2], dx.Count);
    }

    [SkippableTheory]
    [InlineData(1.5f)]
    [InlineData(-0.8f)]
    public async Task Deskewed_word_and_line_boxes_match_the_unrotated_reading(float skew)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        using var page = Image.Load<Rgb24>(Path.Combine(AssetsDir, "lang_en_typed.png"));
        using var skewed = ImagePreprocessor.RotateWithWhiteBackground(page, skew);
        using var service = new PaddleOcrService();

        var on = await service.ExtractTextFromImage(skewed, [OcrLanguage.English], Opts(true));
        var off = await service.ExtractTextFromImage(skewed, [OcrLanguage.English], Opts(false));

        Assert.NotEqual(0f, on.DeskewAngle);
        Assert.Equal(0f, off.DeskewAngle);

        var words = MedianShift(
            on.Lines.SelectMany(l => l.Words).Select(w => (w.Text, w.BoundingBox)),
            off.Lines.SelectMany(l => l.Words).Select(w => (w.Text, w.BoundingBox)));
        var lines = MedianShift(
            on.Lines.Select(l => (l.Text, l.BoundingBox)),
            off.Lines.Select(l => (l.Text, l.BoundingBox)));

        _out.WriteLine($"skew {skew}°, deskew angle {on.DeskewAngle:F2}°, {skewed.Width}x{skewed.Height}");
        _out.WriteLine($"words: {words.Matched} matched, median shift ({words.Dx:F1}, {words.Dy:F1}) px");
        _out.WriteLine($"lines: {lines.Matched} matched, median shift ({lines.Dx:F1}, {lines.Dy:F1}) px");

        Assert.True(words.Matched >= 10, $"too few words matched ({words.Matched})");
        Assert.InRange(Math.Abs(words.Dx), 0, 3);
        Assert.InRange(Math.Abs(words.Dy), 0, 3);
        if (lines.Matched > 0)
        {
            Assert.InRange(Math.Abs(lines.Dx), 0, 3);
            Assert.InRange(Math.Abs(lines.Dy), 0, 3);
        }
    }
}
