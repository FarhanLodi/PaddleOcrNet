using System.Diagnostics;
using PaddleOcrNet.Services;
using PaddleOcrNet.Structure;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Golden-output + warm-timing harness for structure-pipeline performance work. Runs
/// <see cref="PaddleOcrService.AnalyzeDocumentAsync(string, StructureOptions?, CancellationToken)"/> with the
/// default <see cref="StructureOptions"/> over a handful of varied assets and writes the full structure JSON,
/// the Markdown and the warm timings (median of 3) under <c>%TEMP%/paddle-agent-e/&lt;label&gt;</c>, where the
/// label comes from <c>PADDLEOCRNET_GOLDEN_LABEL</c> (default <c>run</c>). Diff two labels to prove a
/// performance change left the output byte-identical. Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StructurePerfGoldenTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";
    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private static readonly string[] Assets =
    {
        "medal_table.png",      // table page
        "doc_with_formula.png", // formula page
        "book.jpg",             // multi-column page
        "seal.png",             // seal
        "Image2.png",           // form-like page
    };

    private readonly ITestOutputHelper _out;
    public StructurePerfGoldenTests(ITestOutputHelper output) => _out = output;

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

    [SkippableFact]
    public async Task Structure_golden_dump_and_warm_timings()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        string label = Environment.GetEnvironmentVariable("PADDLEOCRNET_GOLDEN_LABEL") is { Length: > 0 } l ? l : "run";
        string outDir = Path.Combine(Path.GetTempPath(), "paddle-agent-e", label);
        Directory.CreateDirectory(outDir);

        await using var service = new PaddleOcrService();
        var options = StructureOptions.Default;
        var report = new List<string>();

        foreach (var asset in Assets)
        {
            var path = Path.Combine(RepoRoot, "test", "Assets", asset);
            Skip.IfNot(File.Exists(path), $"{asset} missing.");

            // Cold run (model loads) produces the golden output; three warm runs are timed and re-checked.
            var first = await service.AnalyzeDocumentAsync(path, options);
            string json = first.ToJson();
            string md = first.ToMarkdown();
            await File.WriteAllTextAsync(Path.Combine(outDir, asset + ".json"), json);
            await File.WriteAllTextAsync(Path.Combine(outDir, asset + ".md"), md);

            var times = new List<double>(3);
            bool stable = true;
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                var again = await service.AnalyzeDocumentAsync(path, options);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                stable &= again.ToJson() == json && again.ToMarkdown() == md;
            }

            times.Sort();
            string line = $"{asset}\tblocks={first.Blocks.Count}\tmedian_ms={times[1]:0}\truns={string.Join('/', times.Select(t => t.ToString("0")))}\tstable={stable}";
            report.Add(line);
            _out.WriteLine(line);
        }

        await File.WriteAllLinesAsync(Path.Combine(outDir, "timings.txt"), report);
        _out.WriteLine($"Golden output written to {outDir}");
    }
}
