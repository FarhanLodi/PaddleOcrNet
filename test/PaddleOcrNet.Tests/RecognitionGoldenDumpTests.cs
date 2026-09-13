using System.Diagnostics;
using System.Globalization;
using System.Text;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Golden-output and warm-timing harness for the recognition stage. OCRs a fixed, varied asset set with
/// default options and — when <c>PADDLEOCRNET_GOLDEN_DIR</c> is set — writes every line's text, full
/// round-trip confidence and polygon to <c>&lt;dir&gt;/&lt;asset&gt;.txt</c> plus a <c>timings.txt</c>
/// (median of 3 warm runs), so a performance refactor can be diffed byte-for-byte against the base build.
/// Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RecognitionGoldenDumpTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";

    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private readonly ITestOutputHelper _out;

    public RecognitionGoldenDumpTests(ITestOutputHelper output) => _out = output;

    private static readonly (string File, OcrLanguage Language)[] Assets =
    {
        ("ocr_test1.png", OcrLanguage.English),
        ("sample.png", OcrLanguage.English),
        ("lang_zh_typed.png", OcrLanguage.ChineseSimplified),
        ("paddleocrnet_100_test_dataset/013_multi_column.png", OcrLanguage.English),
        ("paddleocrnet_100_test_dataset/018_multi_column.png", OcrLanguage.English),
        ("lang_hi_typed.png", OcrLanguage.Hindi),
        ("book_rot180.jpg", OcrLanguage.English),
    };

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

    [SkippableFact]
    public async Task Golden_dump_and_warm_timings()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        string? outDir = Environment.GetEnvironmentVariable("PADDLEOCRNET_GOLDEN_DIR");
        if (outDir is not null) Directory.CreateDirectory(outDir);
        var options = RecognitionOptions.Default with
        {
            ReturnWordBoxes = Environment.GetEnvironmentVariable("PADDLEOCRNET_GOLDEN_WORDS") is "1",
        };

        using var service = new PaddleOcrService();
        var timings = new StringBuilder();
        double total = 0;

        foreach (var (file, language) in Assets)
        {
            string path = Path.Combine(AssetsDir, file);
            // Warm-up run (model loads, first-touch allocations), then three timed runs.
            OcrResult result = await service.ExtractTextFromImage(path, new[] { language }, options);
            var runs = new double[3];
            for (int i = 0; i < runs.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                var again = await service.ExtractTextFromImage(path, new[] { language }, options);
                runs[i] = sw.Elapsed.TotalMilliseconds;
                Assert.Equal(Dump(result, false), Dump(again, false));
            }
            Array.Sort(runs);
            total += runs[1];
            timings.Append(file).Append('\t').Append(runs[1].ToString("F0", CultureInfo.InvariantCulture)).Append(" ms\n");
            _out.WriteLine($"{file}: {result.Lines.Count} lines, median {runs[1]:F0} ms");

            Assert.NotEmpty(result.Lines);
            if (outDir is not null)
            {
                File.WriteAllText(Path.Combine(outDir, Path.GetFileName(file) + ".txt"), Dump(result, false));
                if (options.ReturnWordBoxes)
                    File.WriteAllText(Path.Combine(outDir, Path.GetFileName(file) + ".words.txt"), Dump(result, true));
            }
        }

        timings.Append("TOTAL\t").Append(total.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms\n");
        _out.WriteLine(timings.ToString());
        if (outDir is not null) File.WriteAllText(Path.Combine(outDir, "timings.txt"), timings.ToString());
    }

    private static string Dump(OcrResult result, bool words)
    {
        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            sb.Append(line.Confidence.ToString("R", CultureInfo.InvariantCulture)).Append('\t').Append(line.Text).Append('\t');
            foreach (var p in line.BoundingPolygon)
                sb.Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(p.Y.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
            sb.Append('\n');
            if (!words) continue;
            foreach (var w in line.Words)
            {
                sb.Append("    ").Append(w.Confidence.ToString("R", CultureInfo.InvariantCulture)).Append('\t').Append(w.Text).Append('\t');
                foreach (var p in w.BoundingPolygon)
                    sb.Append(p.X.ToString("F1", CultureInfo.InvariantCulture)).Append(',').Append(p.Y.ToString("F1", CultureInfo.InvariantCulture)).Append(' ');
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }
}
