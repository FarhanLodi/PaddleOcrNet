using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Accuracy benchmark over the 100-image corpus in <c>test/Assets/paddleocrnet_100_test_dataset/</c>
/// (11 categories: plain text, multi-column, tables, forms, receipts, seals, dense mixed, low quality,
/// rotated/perspective, numbers/codes and handwriting-like renders).
/// <para>
/// The fixtures are synthetic renders with <b>known</b> content, so this goes beyond "OCR did not crash":
/// it asserts that specific strings printed on the page come back — the pangram and e-mail address on the
/// plain-text pages, the <c>abcdefghijklmnopqrstuvwxyz 0123456789</c> character probe on the low-quality
/// ones, and the <c>OCR TEST</c> banner on every titled page — plus per-image and corpus-wide confidence
/// floors. Thresholds sit below the levels measured when the suite was written (100/100 images produced
/// text, corpus mean confidence 0.963, every plain-text and low-quality anchor recovered) so they catch a
/// real regression without flaking on ordinary model variation.
/// </para>
/// <para>
/// Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>. Note the corpus lives in a SUB-directory, so
/// <see cref="AssetsOcrTests"/> (top-directory only) does not already cover it.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class OcrDatasetBenchmarkTests : IClassFixture<OcrDatasetBenchmarkTests.CorpusFixture>
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";
    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    /// <summary>Minimum mean confidence for any single page. Lowest measured was 0.840 (multi-column).</summary>
    private const double MinPageConfidence = 0.70;

    /// <summary>Minimum mean confidence across the whole corpus. Measured: 0.963.</summary>
    private const double MinCorpusConfidence = 0.92;

    /// <summary>Minimum share of titled pages whose <c>OCR TEST</c> banner is recovered. Measured: 98%.</summary>
    private const double MinTitleHitRate = 0.85;

    private readonly CorpusFixture _corpus;
    private readonly ITestOutputHelper _out;

    public OcrDatasetBenchmarkTests(CorpusFixture corpus, ITestOutputHelper output)
    {
        _corpus = corpus;
        _out = output;
    }

    internal static string RepoRoot
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

    internal static string CorpusDir =>
        Path.Combine(RepoRoot, "test", "Assets", "paddleocrnet_100_test_dataset");

    public static IEnumerable<object[]> Images()
    {
        if (!Directory.Exists(CorpusDir)) yield break;
        foreach (var path in Directory.EnumerateFiles(CorpusDir, "*.png")
                     .OrderBy(p => p, StringComparer.Ordinal))
            yield return new object[] { Path.GetFileName(path) };
    }

    /// <summary>One recognized page: its category, line count, mean confidence and whitespace-collapsed text.</summary>
    public sealed record Page(string Name, string Category, int Lines, double Confidence, string Text);

    /// <summary>
    /// Recognizes the whole corpus once and shares it across the cases. Recognition is kicked off lazily on
    /// first use so a skipped (non-integration) run costs nothing.
    /// </summary>
    public sealed class CorpusFixture : IAsyncDisposable
    {
        private readonly PaddleOcrService _service = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IReadOnlyDictionary<string, Page>? _pages;

        public async Task<IReadOnlyDictionary<string, Page>> PagesAsync()
        {
            if (_pages is not null) return _pages;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_pages is not null) return _pages;

                var pages = new Dictionary<string, Page>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in Directory.EnumerateFiles(CorpusDir, "*.png")
                             .OrderBy(p => p, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(path);
                    var result = await _service.ExtractTextFromImage(path, OcrLanguage.English).ConfigureAwait(false);
                    pages[name] = new Page(
                        name,
                        CategoryOf(name),
                        result.Lines.Count,
                        result.Lines.Count == 0 ? 0 : result.Lines.Average(l => l.Confidence),
                        Collapse(result.FullText));
                }
                return _pages = pages;
            }
            finally { _gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _service.DisposeAsync().ConfigureAwait(false);
            _gate.Dispose();
        }
    }

    /// <summary>"013_multi_column.png" -> "multi_column".</summary>
    internal static string CategoryOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        int us = stem.IndexOf('_');
        return us >= 0 && us + 1 < stem.Length ? stem[(us + 1)..] : stem;
    }

    private static string Collapse(string s)
        => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool Has(Page p, string needle)
        => p.Text.Contains(Collapse(needle), StringComparison.OrdinalIgnoreCase);

    // =================================================================================================

    /// <summary>
    /// Every page in the corpus must recognize some text at a usable confidence. A page that suddenly
    /// returns nothing is the loudest possible OCR regression, and per-file cases name the culprit directly.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Images))]
    public async Task Every_dataset_page_is_recognized(string fileName)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        Skip.IfNot(Directory.Exists(CorpusDir), "100-image dataset not present.");

        var page = (await _corpus.PagesAsync())[fileName];
        _out.WriteLine($"[{page.Category}] {page.Lines} line(s), conf {page.Confidence:0.000}, {page.Text.Length} chars");

        Assert.True(page.Lines > 0, $"{fileName}: no text recognized at all");
        Assert.True(page.Confidence >= MinPageConfidence,
            $"{fileName}: mean confidence {page.Confidence:0.000} below {MinPageConfidence}");
    }

    /// <summary>
    /// The clean plain-text pages carry a known pangram, e-mail address and product name; all three must be
    /// recovered verbatim. These are the easiest pages in the corpus — anything less is a recognition
    /// regression, not model variance.
    /// </summary>
    [SkippableFact]
    public async Task Plain_text_pages_recover_their_known_strings()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        Skip.IfNot(Directory.Exists(CorpusDir), "100-image dataset not present.");

        var pages = (await _corpus.PagesAsync()).Values.Where(p => p.Category == "plain_text").ToList();
        Assert.NotEmpty(pages);

        foreach (var p in pages)
        {
            Assert.True(Has(p, "quick brown fox"), $"{p.Name}: pangram not recovered");
            Assert.True(Has(p, "test.user@example.com"), $"{p.Name}: e-mail not recovered");
            Assert.True(Has(p, "PaddleOCRNet"), $"{p.Name}: product name not recovered");
        }
        _out.WriteLine($"{pages.Count} plain-text pages: pangram + e-mail + product name all recovered");
    }

    /// <summary>
    /// The low-quality pages print a full lowercase alphabet followed by the ten digits — a direct
    /// character-level probe. Both halves must come back on every one of them.
    /// </summary>
    [SkippableFact]
    public async Task Low_quality_pages_recover_the_character_probe()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        Skip.IfNot(Directory.Exists(CorpusDir), "100-image dataset not present.");

        var pages = (await _corpus.PagesAsync()).Values.Where(p => p.Category == "low_quality").ToList();
        Assert.NotEmpty(pages);

        foreach (var p in pages)
        {
            Assert.True(Has(p, "abcdefghijklmnopqrstuvwxyz"), $"{p.Name}: alphabet probe not recovered");
            Assert.True(Has(p, "0123456789"), $"{p.Name}: digit probe not recovered");
            Assert.True(Has(p, "PaddleOCRNet benchmark"), $"{p.Name}: benchmark line not recovered");
        }
        _out.WriteLine($"{pages.Count} low-quality pages: alphabet + digits + benchmark line all recovered");
    }

    /// <summary>
    /// Corpus-wide quality: no page may come back empty, the mean confidence must hold up, and the
    /// <c>OCR TEST</c> banner must be recovered on the overwhelming majority of the titled pages. Prints a
    /// per-category breakdown so a drop is diagnosable from the test log alone.
    /// </summary>
    [SkippableFact]
    public async Task Corpus_quality_holds_across_every_category()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        Skip.IfNot(Directory.Exists(CorpusDir), "100-image dataset not present.");

        var pages = (await _corpus.PagesAsync()).Values.ToList();
        Assert.NotEmpty(pages);

        _out.WriteLine($"{"category",-22} {"n",3} {"minLines",8} {"minConf",8} {"meanConf",8} {"title",6}");
        foreach (var g in pages.GroupBy(p => p.Category).OrderBy(g => g.Key))
        {
            double titleRate = g.Count(p => Has(p, "OCR TEST")) / (double)g.Count();
            _out.WriteLine($"{g.Key,-22} {g.Count(),3} {g.Min(p => p.Lines),8} " +
                           $"{g.Min(p => p.Confidence),8:0.000} {g.Average(p => p.Confidence),8:0.000} {titleRate,6:P0}");
        }

        var empty = pages.Where(p => p.Lines == 0).Select(p => p.Name).ToList();
        Assert.True(empty.Count == 0, "pages with no recognized text: " + string.Join(", ", empty));

        double corpusConfidence = pages.Average(p => p.Confidence);
        _out.WriteLine($"\ncorpus mean confidence {corpusConfidence:0.000} over {pages.Count} pages");
        Assert.True(corpusConfidence >= MinCorpusConfidence,
            $"corpus mean confidence {corpusConfidence:0.000} below {MinCorpusConfidence}");

        // "low_quality" is the one category rendered without the banner.
        var titled = pages.Where(p => p.Category != "low_quality").ToList();
        double hitRate = titled.Count(p => Has(p, "OCR TEST")) / (double)titled.Count;
        _out.WriteLine($"title banner recovered on {hitRate:P0} of {titled.Count} titled pages");
        Assert.True(hitRate >= MinTitleHitRate,
            $"title banner recovered on only {hitRate:P0} of titled pages (floor {MinTitleHitRate:P0})");
    }
}
