using System.Diagnostics;
using System.Globalization;
using System.Text;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Microsoft.ML.OnnxRuntime;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Internal.Detection;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Golden-output harness for detection performance work: OCRs a fixed set of varied assets with default
/// options and dumps line text, confidences ("R" precision), polygons and the raw detector quads to a
/// scratch directory so a before/after run can be diffed byte-for-byte. Also records warm detection and
/// end-to-end timings (median of 3). Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>; the output
/// directory comes from <c>PADDLEOCRNET_GOLDEN_DIR</c> (default <c>%TEMP%/paddle-agent-b</c>) and the file
/// tag from <c>PADDLEOCRNET_GOLDEN_TAG</c> (default <c>run</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DetectionGoldenDumpTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";

    private static readonly string[] Assets =
    {
        "ocr_test1.png",
        "sample.png",
        "lang_zh_typed.png",
        "paddleocrnet_100_test_dataset/013_multi_column.png",
        "Image4.png",
        "lang_hi_typed.png",
    };

    private readonly ITestOutputHelper _out;

    public DetectionGoldenDumpTests(ITestOutputHelper output) => _out = output;

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

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    [SkippableFact]
    public async Task Dump_golden_outputs()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE", $"Set {Gate}=1.");

        string outDir = Environment.GetEnvironmentVariable("PADDLEOCRNET_GOLDEN_DIR")
            ?? Path.Combine(Path.GetTempPath(), "paddle-agent-b");
        string tag = Environment.GetEnvironmentVariable("PADDLEOCRNET_GOLDEN_TAG") ?? "run";
        Directory.CreateDirectory(outDir);

        var golden = new StringBuilder();
        var timings = new StringBuilder();

        // Raw detector quads straight from DbTextDetector (CPU, default detection options).
        string detPath = await ModelDownloadManager.EnsureModelAsync(
            PaddleModelRegistry.MobileDetector, null, new ModelDownloadOptions(), null, CancellationToken.None);
        using (var so = ExecutionProviderResolver.BuildSessionOptions(OcrExecutionProvider.Cpu, new PaddleEngineOptions(), null))
        using (var detector = new DbTextDetector(new InferenceSession(detPath, so)))
        {
            foreach (var asset in Assets)
            {
                using var image = Image.Load<Rgb24>(Path.Combine(RepoRoot, "test", "Assets", asset));
                var quads = detector.Detect(image, DetectionOptions.Default);
                golden.Append("DET ").Append(asset).Append(' ').Append(quads.Count).Append('\n');
                foreach (var q in quads)
                {
                    golden.Append("  q");
                    foreach (var p in q.Points) golden.Append(' ').Append(F(p.X)).Append(',').Append(F(p.Y));
                    golden.Append(" s=").Append(F(q.Score)).Append('\n');
                }

                var samples = new List<double>();
                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    detector.Detect(image, DetectionOptions.Default);
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                }
                samples.Sort();
                timings.Append("det ").Append(asset).Append(' ').Append(samples[1].ToString("F1", CultureInfo.InvariantCulture)).Append(" ms\n");
            }
        }

        // Full pipeline with default options.
        using (var service = new PaddleOcrService())
        {
            foreach (var asset in Assets)
            {
                string path = Path.Combine(RepoRoot, "test", "Assets", asset);
                var result = await service.ExtractTextFromImage(path);
                golden.Append("OCR ").Append(asset).Append(' ').Append(result.Lines.Count).Append('\n');
                foreach (var line in result.Lines)
                {
                    golden.Append("  ").Append(line.Text.Replace("\n", "\\n")).Append(" | c=").Append(F(line.Confidence)).Append(" |");
                    foreach (var p in line.BoundingPolygon) golden.Append(' ').Append(F(p.X)).Append(',').Append(F(p.Y));
                    golden.Append('\n');
                }

                if (Environment.GetEnvironmentVariable("PADDLEOCRNET_GOLDEN_SKIP_OCR_TIMING") is "1") continue;
                var samples = new List<double>();
                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    await service.ExtractTextFromImage(path);
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                }
                samples.Sort();
                timings.Append("ocr ").Append(asset).Append(' ').Append(samples[1].ToString("F1", CultureInfo.InvariantCulture)).Append(" ms\n");
            }
        }

        File.WriteAllText(Path.Combine(outDir, $"golden-{tag}.txt"), golden.ToString());
        File.WriteAllText(Path.Combine(outDir, $"timings-{tag}.txt"), timings.ToString());
        _out.WriteLine(timings.ToString());
    }
}
