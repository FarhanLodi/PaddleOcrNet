using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Internal.Classification;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-backed checks for the recognition stage: word boxes lie inside their lines and run in reading
/// order without changing any line result, concurrent calls with different character filters keep their own
/// filter on the shared recognizer, and batched text-line classification matches one-crop-at-a-time
/// classification. Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RecognitionIntegrationTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";

    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private readonly ITestOutputHelper _out;

    public RecognitionIntegrationTests(ITestOutputHelper output) => _out = output;

    private static string Asset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaddleOcrNet.sln")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repo root not found"), "test", "Assets", name);
    }

    [SkippableFact]
    public async Task Word_boxes_lie_inside_their_lines_and_run_in_reading_order()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        using var service = new PaddleOcrService();
        var assets = new[]
        {
            ("ocr_test1.png", OcrLanguage.English),
            ("lang_zh_typed.png", OcrLanguage.ChineseSimplified),
            ("book_rot180.jpg", OcrLanguage.English),
        };

        foreach (var (file, language) in assets)
        {
            var plainOptions = RecognitionOptions.Default with { Grouping = TextGrouping.Word };
            var plain = await service.ExtractTextFromImage(Asset(file), new[] { language }, plainOptions);
            var detailed = await service.ExtractTextFromImage(Asset(file), new[] { language }, plainOptions with { ReturnWordBoxes = true });

            // Word boxes never change a line's text, confidence or geometry.
            Assert.Equal(
                plain.Lines.Select(l => (l.Text, l.Confidence, l.BoundingBox)),
                detailed.Lines.Select(l => (l.Text, l.Confidence, l.BoundingBox)));

            int words = 0;
            foreach (var line in detailed.Lines)
            {
                Assert.NotEmpty(line.Words);
                words += line.Words.Count;
                Assert.Equal(Regex.Replace(line.Text, @"\s", ""), string.Concat(line.Words.Select(w => w.Text)));

                var box = line.BoundingBox;
                const double tolerance = 2;
                foreach (var word in line.Words)
                {
                    Assert.True(word.BoundingBox.MinX >= box.MinX - tolerance && word.BoundingBox.MaxX <= box.MaxX + tolerance
                        && word.BoundingBox.MinY >= box.MinY - tolerance && word.BoundingBox.MaxY <= box.MaxY + tolerance,
                        $"[{file}] word '{word.Text}' {word.BoundingBox} escapes line '{line.Text}' {box}");
                }

                // Successive word centres advance along the line's reading direction (its longer edge from the
                // first polygon point, which follows the text direction even on a re-oriented page).
                var p = line.BoundingPolygon;
                var along = (X: p[1].X - p[0].X, Y: p[1].Y - p[0].Y);
                var down = (X: p[3].X - p[0].X, Y: p[3].Y - p[0].Y);
                var dir = Length(along) >= Length(down) ? along : down;
                double norm = Length(dir);
                double previous = double.NegativeInfinity;
                foreach (var word in line.Words)
                {
                    double projection = ((word.BoundingBox.CenterX - p[0].X) * dir.X + (word.BoundingBox.CenterY - p[0].Y) * dir.Y) / norm;
                    Assert.True(projection >= previous - 1, $"[{file}] word '{word.Text}' is out of order in '{line.Text}'");
                    previous = projection;
                }
            }

            _out.WriteLine($"{file}: {detailed.Lines.Count} lines, {words} words");
        }
    }

    private static double Length((double X, double Y) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

    [SkippableFact]
    public async Task Concurrent_calls_with_different_allowlists_keep_their_own_filters()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        using var service = new PaddleOcrService();
        string path = Asset("ocr_test1.png");
        var languages = new[] { OcrLanguage.English };
        var digits = RecognitionOptions.Default with { Allowlist = RecognitionOptions.FromCharacters("0123456789") };

        string expectedDigits = (await service.ExtractTextFromImage(path, languages, digits)).FullText;
        string expectedAll = (await service.ExtractTextFromImage(path, languages, RecognitionOptions.Default)).FullText;
        Assert.NotEqual(expectedDigits, expectedAll);

        var calls = Enumerable.Range(0, 6)
            .Select(i => (Digits: i % 2 == 0, Task: Task.Run(() => service.ExtractTextFromImage(
                path, languages, i % 2 == 0 ? digits : RecognitionOptions.Default))))
            .ToArray();
        await Task.WhenAll(calls.Select(c => c.Task));

        foreach (var (isDigits, task) in calls)
            Assert.Equal(isDigits ? expectedDigits : expectedAll, task.Result.FullText);
    }

    [SkippableFact]
    public async Task Batched_classifier_verdicts_match_single_crop_classification()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        string modelPath = await ModelDownloadManager.EnsureModelAsync(
            PaddleModelRegistry.Classifier, null, new ModelDownloadOptions(), null, CancellationToken.None);
        using var classifier = new TextLineClassifier(new InferenceSession(modelPath));

        var crops = new List<Image<Rgb24>>();
        try
        {
            foreach (var name in new[] { "ocr_test1.png", "textline_rot180.jpg", "sample.png" })
            {
                using var source = Image.Load<Rgb24>(Asset(name));
                int strips = 5;
                int stripHeight = Math.Max(8, source.Height / strips);
                for (int s = 0; s < strips && s * stripHeight + 8 <= source.Height; s++)
                {
                    int h = Math.Min(stripHeight, source.Height - s * stripHeight);
                    crops.Add(source.Clone(c => c.Crop(new Rectangle(0, s * stripHeight, source.Width, h))));
                }
                crops.Add(source.Clone());
            }

            var single = crops.Select(classifier.Classify).ToArray();
            var batched = classifier.Classify(crops, maxDegreeOfParallelism: 4);

            double maxDiff = 0;
            for (int i = 0; i < crops.Count; i++)
            {
                Assert.Equal(single[i].Rotated, batched[i].Rotated);
                maxDiff = Math.Max(maxDiff, Math.Abs(single[i].Score - batched[i].Score));
            }
            _out.WriteLine($"{crops.Count} crops, max |score(single) - score(batched)| = {maxDiff:R}");
            Assert.True(maxDiff < 1e-4, $"batched scores drifted by {maxDiff}");
        }
        finally
        {
            foreach (var crop in crops) crop.Dispose();
        }
    }
}
