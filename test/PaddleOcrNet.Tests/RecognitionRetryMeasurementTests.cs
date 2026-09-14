using System.Text;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Measures <see cref="RecognitionOptions.RetryBelowConfidence"/> on the hardest pages of the bundled corpus
/// (low-quality, rotated/perspective and handwriting-like renders): how many weak lines were re-read, how
/// many alternates were accepted, the mean line confidence with and without the retry, and — the guard —
/// that no known anchor string recovered without the retry is lost with it. Line count and order never
/// change. Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RecognitionRetryMeasurementTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";

    /// <summary>Retry threshold under measurement; override with <c>PADDLEOCRNET_RETRY_THRESHOLD</c>.</summary>
    private static readonly double Threshold =
        double.TryParse(Environment.GetEnvironmentVariable("PADDLEOCRNET_RETRY_THRESHOLD"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t) ? t : 0.95;

    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private static readonly string[] Anchors =
    {
        "abcdefghijklmnopqrstuvwxyz", "0123456789", "PaddleOCRNet benchmark", "OCR TEST",
        "quick brown fox", "test.user@example.com",
    };

    private readonly ITestOutputHelper _out;

    public RecognitionRetryMeasurementTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public async Task Retry_only_polishes_weak_lines_and_never_loses_an_anchor()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        string corpus = OcrDatasetBenchmarkTests.CorpusDir;
        Skip.IfNot(Directory.Exists(corpus), "100-image dataset not present.");

        var pages = Directory.EnumerateFiles(corpus, "*.png")
            .Where(p => OcrDatasetBenchmarkTests.CategoryOf(Path.GetFileName(p)) is "low_quality" or "rotated_perspective" or "handwriting_like")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        // Handwritten and photographed English assets carry the weakest lines in test/Assets.
        string assets = Path.GetDirectoryName(corpus)!;
        pages.AddRange(new[] { "lang_en_handwritten.png", "ocr_test2.png", "ocr_test3.png", "Image10.jpg", "Image11.jpg" }
            .Select(n => Path.Combine(assets, n))
            .Where(File.Exists));

        using var service = new PaddleOcrService();
        var languages = new[] { OcrLanguage.English };
        // Word grouping keeps one line per recognized crop, so a retried crop is never averaged into a merged
        // line whose confidence sits above the threshold.
        var baseline = RecognitionOptions.Default with { Grouping = TextGrouping.Word };
        var retry = baseline with { RetryBelowConfidence = Threshold };
        var report = new StringBuilder();
        var violations = new List<string>();
        int weak = 0, replaced = 0, lines = 0;
        double sumBefore = 0, sumAfter = 0;

        foreach (var path in pages)
        {
            var before = await service.ExtractTextFromImage(path, languages, baseline);
            var after = await service.ExtractTextFromImage(path, languages, retry);

            Assert.Equal(before.Lines.Count, after.Lines.Count);
            string name = Path.GetFileName(path);
            for (int i = 0; i < before.Lines.Count; i++)
            {
                var b = before.Lines[i];
                var a = after.Lines[i];
                Assert.Equal(b.BoundingBox, a.BoundingBox);
                lines++;
                sumBefore += b.Confidence;
                sumAfter += a.Confidence;
                if (b.Confidence < Threshold) weak++;
                if (b.Text != a.Text || b.Confidence != a.Confidence)
                {
                    replaced++;
                    if (b.Confidence >= Threshold)
                        violations.Add($"{name}: line above the threshold changed: [{b.Confidence:R}] '{b.Text}' -> [{a.Confidence:R}] '{a.Text}'");
                    report.AppendLine($"  {name}: [{b.Confidence:0.000}] '{b.Text}' -> [{a.Confidence:0.000}] '{a.Text}'");
                }
            }

            string beforeText = Collapse(before.FullText), afterText = Collapse(after.FullText);
            foreach (var anchor in Anchors)
            {
                bool had = beforeText.Contains(anchor, StringComparison.OrdinalIgnoreCase);
                bool has = afterText.Contains(anchor, StringComparison.OrdinalIgnoreCase);
                if (had && !has) violations.Add($"{name}: retry lost the anchor '{anchor}'");
                if (!had && has) report.AppendLine($"  {name}: retry recovered the anchor '{anchor}'");
            }
        }

        _out.WriteLine($"{pages.Count} pages, {lines} lines, {weak} below {Threshold}, {replaced} replaced");
        _out.WriteLine($"mean line confidence {sumBefore / Math.Max(1, lines):0.0000} -> {sumAfter / Math.Max(1, lines):0.0000}");
        _out.WriteLine(report.ToString());

        string? dir = Environment.GetEnvironmentVariable("PADDLEOCRNET_GOLDEN_DIR");
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "retry-measurement.txt"),
                $"{pages.Count} pages, {lines} lines, {weak} below {Threshold}, {replaced} replaced\n" +
                $"mean line confidence {sumBefore / Math.Max(1, lines):0.0000} -> {sumAfter / Math.Max(1, lines):0.0000}\n" + report +
                string.Join("\n", violations.Select(v => "VIOLATION " + v)));
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    private static string Collapse(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
