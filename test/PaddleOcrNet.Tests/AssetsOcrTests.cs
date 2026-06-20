using PaddleOcrNet.Models;
using PaddleOcrNet.Pdf;
using PaddleOcrNet.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Data-driven OCR coverage over every asset in <c>test/Assets/</c>. Each image and PDF dropped into that
/// folder is automatically exercised through the real det → cls → rec pipeline — no per-file edits needed.
/// <para>
/// Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c> and the <c>Integration</c> trait because it downloads
/// the PP-OCRv5 ONNX models from HuggingFace on first run; the default unit suite stays model-free.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssetsOcrTests : IClassFixture<AssetsOcrTests.ServiceFixture>
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";

    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private readonly ServiceFixture _fixture;
    private readonly ITestOutputHelper _out;

    public AssetsOcrTests(ServiceFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _out = output;
    }

    /// <summary>A single shared service for the whole class so models load once, not per test case.</summary>
    public sealed class ServiceFixture : IDisposable
    {
        public PaddleOcrService Service { get; } = new();
        public void Dispose() => Service.Dispose();
    }

    // Walk up from the test binary to the repo root (folder containing PaddleOcrNet.sln).
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaddleOcrNet.sln")))
                dir = dir.Parent;
            return dir?.FullName
                ?? throw new InvalidOperationException("Could not locate repo root (PaddleOcrNet.sln not found).");
        }
    }

    private static string AssetsDir => Path.Combine(RepoRoot, "test", "Assets");

    public static IEnumerable<object[]> Images()
    {
        var dir = AssetsDir;
        if (!Directory.Exists(dir)) yield break;
        string[] exts = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                     .Where(p => exts.Contains(Path.GetExtension(p).ToLowerInvariant()))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            yield return new object[] { Path.GetFileName(path) };
    }

    public static IEnumerable<object[]> Pdfs()
    {
        var dir = Path.Combine(AssetsDir, "pdf");
        if (!Directory.Exists(dir)) yield break;
        foreach (var path in Directory.EnumerateFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            yield return new object[] { Path.GetFileName(path) };
    }

    [SkippableTheory]
    [MemberData(nameof(Images))]
    public async Task Ocr_runs_on_every_asset_image(string fileName)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        var path = Path.Combine(AssetsDir, fileName);
        OcrResult result = await _fixture.Service.ExtractTextFromImage(path, new[] { "en" });

        Assert.NotNull(result);
        Assert.True(result.SourceWidth > 0 && result.SourceHeight > 0, "source dimensions should be populated");

        _out.WriteLine($"[{fileName}] {result.Lines.Count} line(s), {result.SourceWidth}x{result.SourceHeight}");
        foreach (var line in result.Lines)
            _out.WriteLine($"  [{line.Confidence:0.00}] {line.Text}");
    }

    [SkippableTheory]
    [MemberData(nameof(Images))]
    public async Task AutoDetect_runs_on_every_asset_image(string fileName)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        var path = Path.Combine(AssetsDir, fileName);
        OcrResult result = await _fixture.Service.ExtractTextFromImage(path, new[] { "auto" });

        Assert.NotNull(result);
        _out.WriteLine($"[{fileName}] detected: [{string.Join(", ", result.DetectedLanguages)}] — {result.Lines.Count} line(s)");
    }

    [SkippableTheory]
    [MemberData(nameof(Pdfs))]
    public async Task Ocr_runs_on_every_asset_pdf(string fileName)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");

        var path = Path.Combine(AssetsDir, "pdf", fileName);
        PdfOcrResult result = await _fixture.Service.ExtractTextFromPdfAsync(path, new[] { "en" });

        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
        _out.WriteLine($"[{fileName}] {result.Pages.Count} page(s), {result.FullText.Length} chars total");
    }
}
