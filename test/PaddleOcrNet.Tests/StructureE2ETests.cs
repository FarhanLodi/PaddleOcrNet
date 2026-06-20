using PaddleOcrNet.Services;
using PaddleOcrNet.Structure;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// End-to-end PP-StructureV3 validation that exercises the real HuggingFace download path for the layout
/// model (PP-DocLayoutV3) and produces a structured document (regions + reading order + Markdown). Gated
/// behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c> and the Integration trait.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StructureE2ETests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";
    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private readonly ITestOutputHelper _out;
    public StructureE2ETests(ITestOutputHelper output) => _out = output;

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
    public async Task AnalyzeDocument_downloads_layout_from_hf_and_returns_structure()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        var path = Path.Combine(RepoRoot, "test", "Assets", "Image2.png");
        Skip.IfNot(File.Exists(path), "Image2.png asset missing.");

        await using var service = new PaddleOcrService();
        var options = new StructureOptions
        {
            LayoutModel = LayoutModel.RtDetrL,   // → PP-DocLayoutV3 on HuggingFace
            Languages = new[] { "en" },
            RecognizeSeals = false,              // no seal model hosted yet
        };

        var result = await service.AnalyzeDocumentAsync(path, options);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Blocks);

        _out.WriteLine($"{result.Blocks.Count} blocks ({result.SourceWidth}x{result.SourceHeight}):");
        foreach (var b in result.Blocks)
            _out.WriteLine($"  #{b.Order} {b.Type} conf={b.Score:0.00} text={(b.Text ?? "").Replace('\n', ' ')}");

        _out.WriteLine("\n--- Markdown (first 800 chars) ---");
        var md = result.ToMarkdown();
        _out.WriteLine(md.Length > 800 ? md[..800] : md);
    }
}
