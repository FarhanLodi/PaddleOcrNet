namespace PaddleOcrNet.Tests.Evaluation;

internal sealed record BenchmarkPage(string Id, string DocumentId, string OriginGroup,
    string SourceKind, string Split, string Category, string Reference,
    Dictionary<string, string> SensitiveTokens);
internal sealed record BenchmarkManifest(string DatasetRevision, string License, BenchmarkPage[] Pages);
internal sealed record BenchmarkPrediction(string Id, string Text, Dictionary<string, string> SensitiveTokens);
internal sealed record BenchmarkRun(string RepositoryRevision, string Runtime, string Provider,
    string Precision, string Hardware, int Threads, string OptionsJson,
    Dictionary<string, string> ModelAndDictionarySha256, BenchmarkPrediction[] Predictions);
internal sealed record PageScore(string Id, string DocumentId, string SourceKind, string Split,
    string Category, TranscriptionScore Raw, TranscriptionScore Normalized,
    int SensitiveTokensCorrect, int SensitiveTokensTotal);
internal sealed record SliceScore(string SourceKind, string Split, string Category, int Pages,
    int Documents, double? RawCer, double? RawWer, double? NormalizedCer,
    double? NormalizedWer, double ExactPageMatchRate, int BlankPages,
    int BlankPagesWithFalsePositives, int FalsePositiveCharacters,
    int SensitiveTokensCorrect, int SensitiveTokensTotal);
internal sealed record BenchmarkReport(string DatasetRevision, string Normalization,
    BenchmarkRun Provenance, PageScore[] Pages, SliceScore[] Slices);

internal static class TranscriptionBenchmark
{
    public static BenchmarkReport Evaluate(BenchmarkManifest manifest, BenchmarkRun run)
    {
        Require(!string.IsNullOrWhiteSpace(manifest.DatasetRevision), "Dataset revision is required.");
        Require(!string.IsNullOrWhiteSpace(manifest.License), "Dataset license is required.");
        Require(manifest.Pages.Length > 0, "Expected evaluation pages are missing.");
        foreach (string value in new[] { run.RepositoryRevision, run.Runtime, run.Provider,
                     run.Precision, run.Hardware, run.OptionsJson })
            Require(!string.IsNullOrWhiteSpace(value), "Run provenance is incomplete.");
        Require(run.Threads > 0, "Thread count must be positive.");
        Require(run.ModelAndDictionarySha256.Count > 0 && run.ModelAndDictionarySha256.All(p =>
            !string.IsNullOrWhiteSpace(p.Key) && p.Value.Length == 64 && p.Value.All(Uri.IsHexDigit)),
            "Record SHA-256 hashes for every model and dictionary used.");
        Require(manifest.Pages.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == manifest.Pages.Length,
            "Duplicate manifest page IDs.");
        Require(run.Predictions.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == run.Predictions.Length,
            "Duplicate prediction page IDs.");
        Require(manifest.Pages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal)
            .SetEquals(run.Predictions.Select(p => p.Id)), "Missing or unexpected predictions.");
        foreach (var page in manifest.Pages)
        {
            Require(new[] { page.Id, page.DocumentId, page.OriginGroup, page.Category }
                .All(s => !string.IsNullOrWhiteSpace(s)), "Page identity/category is required.");
            Require(page.SourceKind is "real" or "synthetic", "SourceKind must be real or synthetic.");
            Require(page.Split is "training" or "calibration" or "heldout", "Unknown evaluation split.");
        }
        Require(manifest.Pages.GroupBy(p => p.OriginGroup).All(g => g.Select(p => p.Split).Distinct().Count() == 1),
            "Origin/template/provider groups must not cross splits.");
        Require(manifest.Pages.GroupBy(p => p.DocumentId).All(g => g.Select(p => p.Split).Distinct().Count() == 1),
            "Documents must not cross splits.");
        var predictions = run.Predictions.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var pages = manifest.Pages.Select(page =>
        {
            var prediction = predictions[page.Id];
            return new PageScore(page.Id, page.DocumentId, page.SourceKind, page.Split, page.Category,
                TranscriptionScorer.Score(page.Reference, prediction.Text),
                TranscriptionScorer.Score(TranscriptionScorer.Normalize(page.Reference), TranscriptionScorer.Normalize(prediction.Text)),
                page.SensitiveTokens.Count(p => prediction.SensitiveTokens.TryGetValue(p.Key, out var actual) &&
                    string.Equals(p.Value, actual, StringComparison.Ordinal)), page.SensitiveTokens.Count);
        }).ToArray();
        var slices = pages.GroupBy(p => (p.SourceKind, p.Split, p.Category)).Select(g => new SliceScore(
            g.Key.SourceKind, g.Key.Split, g.Key.Category, g.Count(), g.Select(p => p.DocumentId).Distinct().Count(),
            Rate(g.Sum(p => p.Raw.BlankReference ? 0 : p.Raw.CharacterEdits), g.Sum(p => p.Raw.ReferenceCharacters)),
            Rate(g.Sum(p => p.Raw.ReferenceWords == 0 ? 0 : p.Raw.WordEdits), g.Sum(p => p.Raw.ReferenceWords)),
            Rate(g.Sum(p => p.Normalized.BlankReference ? 0 : p.Normalized.CharacterEdits), g.Sum(p => p.Normalized.ReferenceCharacters)),
            Rate(g.Sum(p => p.Normalized.ReferenceWords == 0 ? 0 : p.Normalized.WordEdits), g.Sum(p => p.Normalized.ReferenceWords)),
            (double)g.Count(p => p.Raw.ExactMatch) / g.Count(), g.Count(p => p.Raw.BlankReference),
            g.Count(p => p.Raw.FalsePositiveCharacters > 0), g.Sum(p => p.Raw.FalsePositiveCharacters),
            g.Sum(p => p.SensitiveTokensCorrect), g.Sum(p => p.SensitiveTokensTotal))).ToArray();
        // Predictions remain available in page scores by ID; avoid duplicating document text in provenance.
        return new(manifest.DatasetRevision, TranscriptionScorer.Normalization, run with { Predictions = [] }, pages, slices);
    }

    private static double? Rate(int errors, int count) => count == 0 ? null : (double)errors / count;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
