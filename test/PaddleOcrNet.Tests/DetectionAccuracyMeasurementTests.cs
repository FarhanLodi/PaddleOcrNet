using System.Diagnostics;
using System.Globalization;
using System.Text;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Corpus measurement for the opt-in detection passes (<see cref="DetectionOptions.MinTextHeight"/>,
/// <see cref="DetectionOptions.TileLargeImages"/>, <see cref="DetectionOptions.EnhanceContrast"/>) and
/// the ONNX Runtime spinning setting. Generates degraded variants (50% downscale, contrast-faded, tall
/// concatenated receipt) into a scratch directory, OCRs originals and variants under each configuration,
/// and writes line count, mean confidence, recognized character count and time per image as TSV.
/// Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c> plus <c>PADDLEOCRNET_RUN_DET_MEASURE=1</c>
/// (it is slow); output goes to <c>PADDLEOCRNET_GOLDEN_DIR</c> (default <c>%TEMP%/paddle-agent-b</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DetectionAccuracyMeasurementTests
{
    private readonly ITestOutputHelper _out;

    public DetectionAccuracyMeasurementTests(ITestOutputHelper output) => _out = output;

    private static bool Enabled(string name) => Environment.GetEnvironmentVariable(name) is "1" or "true" or "TRUE";

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaddleOcrNet.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }

    private static string Scratch => Environment.GetEnvironmentVariable("PADDLEOCRNET_GOLDEN_DIR")
        ?? Path.Combine(Path.GetTempPath(), "paddle-agent-b");

    private static string Asset(string name) => Path.Combine(RepoRoot, "test", "Assets", name);

    private static readonly string[] Originals =
    {
        "ocr_test1.png",
        "sample.png",
        "Image1.png",
        "lang_en_typed.png",
        "paddleocrnet_100_test_dataset/013_multi_column.png",
        "paddleocrnet_100_test_dataset/057_receipts_invoices.png",
        "paddleocrnet_100_test_dataset/067_low_quality.png",
        "paddleocrnet_100_test_dataset/081_dense_mixed.png",
    };

    /// <summary>Creates the degraded variants once and returns every image path to measure.</summary>
    private static List<string> BuildCorpus()
    {
        string dir = Path.Combine(Scratch, "variants");
        Directory.CreateDirectory(dir);
        var paths = Originals.Select(Asset).ToList();

        foreach (var name in new[] { "Image1.png", "paddleocrnet_100_test_dataset/013_multi_column.png", "paddleocrnet_100_test_dataset/081_dense_mixed.png", "ocr_test1.png" })
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            string half = Path.Combine(dir, $"half_{stem}.png");
            if (!File.Exists(half))
            {
                using var img = Image.Load<Rgb24>(Asset(name));
                using var small = img.Clone(c => c.Resize(new ResizeOptions
                {
                    Size = new Size(Math.Max(1, img.Width / 2), Math.Max(1, img.Height / 2)),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Triangle,
                }));
                small.SaveAsPng(half);
            }
            paths.Add(half);
        }

        foreach (var name in new[] { "Image1.png", "paddleocrnet_100_test_dataset/013_multi_column.png", "paddleocrnet_100_test_dataset/067_low_quality.png", "lang_en_typed.png" })
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            string faded = Path.Combine(dir, $"faded_{stem}.png");
            if (!File.Exists(faded))
            {
                using var img = Image.Load<Rgb24>(Asset(name));
                // Washed-out scan: compress contrast to 30% around a light grey, with a left-to-right
                // illumination falloff.
                img.ProcessPixelRows(acc =>
                {
                    for (int y = 0; y < acc.Height; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            float shade = 1f - 0.25f * x / Math.Max(1, row.Length - 1);
                            static byte F(byte v, float s) => (byte)Math.Clamp((175 + (v - 128) * 0.3f) * s, 0, 255);
                            row[x] = new Rgb24(F(row[x].R, shade), F(row[x].G, shade), F(row[x].B, shade));
                        }
                    }
                });
                img.SaveAsPng(faded);
            }
            paths.Add(faded);
        }

        string tall = Path.Combine(dir, "tall_receipt.png");
        if (!File.Exists(tall))
        {
            string[] parts = { "057", "059", "061", "063", "065", "057" };
            var images = parts.Select(p => Image.Load<Rgb24>(Asset($"paddleocrnet_100_test_dataset/{p}_receipts_invoices.png"))).ToList();
            try
            {
                int w = images.Max(i => i.Width);
                int h = images.Sum(i => i.Height);
                using var canvas = new Image<Rgb24>(w, h);
                int y = 0;
                foreach (var part in images)
                {
                    var at = new Point(0, y);
                    canvas.Mutate(c => c.DrawImage(part, at, 1f));
                    y += part.Height;
                }
                canvas.SaveAsPng(tall);
            }
            finally
            {
                foreach (var i in images) i.Dispose();
            }
        }
        paths.Add(tall);
        return paths;
    }

    [SkippableFact]
    public async Task Measure_opt_in_detection_passes()
    {
        Skip.IfNot(Enabled("PADDLEOCRNET_RUN_INTEGRATION") && Enabled("PADDLEOCRNET_RUN_DET_MEASURE"), "Set PADDLEOCRNET_RUN_INTEGRATION=1 and PADDLEOCRNET_RUN_DET_MEASURE=1.");

        var corpus = BuildCorpus();
        var configs = new (string Name, DetectionOptions Options)[]
        {
            ("baseline", DetectionOptions.Default),
            ("minh16", DetectionOptions.Default with { MinTextHeight = 16 }),
            ("tile", DetectionOptions.Default with { TileLargeImages = true }),
            ("enhance", DetectionOptions.Default with { EnhanceContrast = true }),
        };

        var tsv = new StringBuilder("config\timage\tlines\tmeanConf\tchars\tms\n");
        using var service = new PaddleOcrService();
        await service.ExtractTextFromImage(corpus[0]); // warm

        foreach (var path in corpus)
        {
            foreach (var (name, det) in configs)
            {
                var sw = Stopwatch.StartNew();
                var result = await service.ExtractTextFromImage(path, new[] { OcrLanguage.Auto }, new RecognitionOptions { Detection = det });
                double ms = sw.Elapsed.TotalMilliseconds;
                double mean = result.Lines.Count == 0 ? 0 : result.Lines.Average(l => l.Confidence);
                int chars = result.Lines.Sum(l => l.Text.Length);
                tsv.Append(name).Append('\t').Append(Path.GetFileName(path)).Append('\t').Append(result.Lines.Count)
                   .Append('\t').Append(mean.ToString("F4", CultureInfo.InvariantCulture)).Append('\t').Append(chars)
                   .Append('\t').Append(ms.ToString("F0", CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        File.WriteAllText(Path.Combine(Scratch, "accuracy.tsv"), tsv.ToString());
        _out.WriteLine(tsv.ToString());
    }

    [SkippableFact]
    public async Task Measure_intra_op_spinning()
    {
        Skip.IfNot(Enabled("PADDLEOCRNET_RUN_INTEGRATION") && Enabled("PADDLEOCRNET_RUN_DET_MEASURE"), "Set PADDLEOCRNET_RUN_INTEGRATION=1 and PADDLEOCRNET_RUN_DET_MEASURE=1.");

        string[] images =
        {
            Asset("ocr_test1.png"),
            Asset("Image1.png"),
            Asset("paddleocrnet_100_test_dataset/013_multi_column.png"),
            Asset("paddleocrnet_100_test_dataset/057_receipts_invoices.png"),
        };

        var report = new StringBuilder("spinning\timage\tmedianMs\n");
        // Two rounds, alternating, so drift from other load on the machine hits both settings.
        var totals = new Dictionary<string, List<double>>();
        for (int round = 0; round < 2; round++)
        {
            foreach (bool? spin in new bool?[] { null, false })
            {
                using var service = new PaddleOcrService(new PaddleOcrServiceOptions { AllowIntraOpSpinning = spin });
                await service.ExtractTextFromImage(images[0]); // load + warm
                foreach (var path in images)
                {
                    var samples = new List<double>();
                    for (int i = 0; i < 3; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        await service.ExtractTextFromImage(path);
                        samples.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    samples.Sort();
                    string key = $"{(spin is null ? "default" : "off")}\t{Path.GetFileName(path)}";
                    if (!totals.TryGetValue(key, out var list)) totals[key] = list = new List<double>();
                    list.Add(samples[1]);
                }
            }
        }

        foreach (var (key, list) in totals.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            report.Append(key).Append('\t').Append(string.Join("/", list.Select(v => v.ToString("F0", CultureInfo.InvariantCulture)))).Append('\n');
        }
        File.WriteAllText(Path.Combine(Scratch, "spinning.tsv"), report.ToString());
        _out.WriteLine(report.ToString());
    }
}
