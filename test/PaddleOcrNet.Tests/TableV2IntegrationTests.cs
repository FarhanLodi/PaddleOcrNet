using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using PaddleOcrNet.Structure.Table;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Gated end-to-end validation of the SLANeXt "table recognition v2" path (PP-LCNet table classifier +
/// SLANeXt wired/wireless structure model). Downloads the three hosted models and runs them on a bordered
/// synthetic table, asserting the classifier picks <i>wired</i> and the structure model recovers a populated
/// HTML grid. Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c> so it stays out of the default unit suite.
/// </summary>
[Trait("Category", "Integration")]
public class TableV2IntegrationTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";

    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaddleOcrNet.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
        }
    }

    private static string Asset(string name) => Path.Combine(RepoRoot, "test", "Assets", name);

    [SkippableFact]
    public async Task SlaNeXt_v2_classifies_wired_and_recovers_a_table_grid()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        var download = new ModelDownloadOptions();
        var clsPath = await ModelDownloadManager.EnsureModelAsync(PaddleModelRegistry.TableClassifier, null, download, null, CancellationToken.None);
        var wiredPath = await ModelDownloadManager.EnsureModelAsync(PaddleModelRegistry.SlaNeXtWired, null, download, null, CancellationToken.None);
        var wirelessPath = await ModelDownloadManager.EnsureModelAsync(PaddleModelRegistry.SlaNeXtWireless, null, download, null, CancellationToken.None);

        using var crop = await Image.LoadAsync<Rgb24>(Asset("synthetic_table.png"));

        // The synthetic table is ruled/bordered -> the classifier must say "wired" (not wireless).
        using (var classifier = new TableClassifier(new InferenceSession(clsPath)))
        {
            Assert.False(classifier.IsWireless(crop), "A bordered table should classify as wired.");
        }

        // Build the full v2 router (it takes ownership of the three sessions) and recover the grid.
        var router = new SlaNeXtTableRouter(
            new TableClassifier(new InferenceSession(clsPath)),
            new SlanetTableRecognizer(new InferenceSession(wiredPath), Array.Empty<string>(), inputSize: 512),
            new SlanetTableRecognizer(new InferenceSession(wirelessPath), Array.Empty<string>(), inputSize: 512));

        try
        {
            var result = router.Recognize(crop, Array.Empty<OcrLine>());

            Assert.Contains("<table>", result.Html);
            Assert.Contains("<td>", result.Html);
            Assert.True(result.CellBounds.Count > 0,
                $"SLANeXt recovered no cells. HTML: {result.Html}");
        }
        finally
        {
            router.Dispose();
        }
    }
}
