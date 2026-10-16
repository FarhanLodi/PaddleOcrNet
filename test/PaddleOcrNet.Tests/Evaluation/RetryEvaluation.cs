using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaddleOcrNet.Tests.Evaluation;

internal sealed record RetryFixture(string Id, string ImagePath, string Reference, string[] CriticalTokens);
internal sealed record RetryManifest(string DatasetRevision, RetryFixture[] Pages);
internal sealed record RetryPageScore(string Id, TranscriptionScore Before, TranscriptionScore After,
    TranscriptionScore NormalizedBefore, TranscriptionScore NormalizedAfter,
    string Outcome, int CriticalTokensRecovered, int CriticalTokensLost);
internal sealed record RetrySummary(int Corrected, int Damaged, int Unchanged, int ChangedEqualError,
    int CriticalTokensRecovered, int CriticalTokensLost);

internal static class RetryEvaluation
{
    // "corrected" means fewer reference edits, not necessarily a fully correct page.
    public static RetryPageScore Score(string id, string reference, string before, string after,
        IReadOnlyList<string> criticalTokens)
    {
        var b = TranscriptionScorer.Score(reference, before);
        var a = TranscriptionScorer.Score(reference, after);
        int recovered = 0, lost = 0;
        foreach (string token in criticalTokens.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("Critical tokens cannot be empty.");
            int expected = Count(reference, token);
            if (expected == 0) throw new InvalidDataException($"Critical token '{token}' is absent from reference '{id}'.");
            int previous = Math.Min(expected, Count(before, token));
            int current = Math.Min(expected, Count(after, token));
            recovered += Math.Max(0, current - previous);
            lost += Math.Max(0, previous - current);
        }
        return new(id, b, a,
            TranscriptionScorer.Score(TranscriptionScorer.Normalize(reference), TranscriptionScorer.Normalize(before)),
            TranscriptionScorer.Score(TranscriptionScorer.Normalize(reference), TranscriptionScorer.Normalize(after)),
            before == after ? "unchanged" : a.CharacterEdits < b.CharacterEdits ? "corrected" :
                a.CharacterEdits > b.CharacterEdits ? "damaged" : "changed-equal-error", recovered, lost);
    }

    public static RetrySummary Summarize(IReadOnlyList<RetryPageScore> pages) => new(
        pages.Count(p => p.Outcome == "corrected"), pages.Count(p => p.Outcome == "damaged"),
        pages.Count(p => p.Outcome == "unchanged"), pages.Count(p => p.Outcome == "changed-equal-error"),
        pages.Sum(p => p.CriticalTokensRecovered), pages.Sum(p => p.CriticalTokensLost));

    public static RetryManifest Load(string path)
    {
        path = Path.GetFullPath(path);
        var manifest = JsonSerializer.Deserialize<RetryManifest>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.DatasetRevision) ||
            manifest.Pages is null || manifest.Pages.Length == 0)
            throw new InvalidDataException("Retry manifest requires a dataset revision and labeled pages.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pages = manifest.Pages.Select(page =>
        {
            if (page is null || string.IsNullOrWhiteSpace(page.Id) || !ids.Add(page.Id) ||
                string.IsNullOrWhiteSpace(page.ImagePath) || page.Reference is null || page.CriticalTokens is null)
                throw new InvalidDataException("Retry fixture requires a unique ID, image path, reference and criticalTokens array.");
            string image = Path.GetFullPath(page.ImagePath, Path.GetDirectoryName(path)!);
            if (!File.Exists(image)) throw new FileNotFoundException($"Missing labeled retry image: {page.Id}", image);
            // Validate all labels before any model loading or inference.
            _ = Score(page.Id, page.Reference, page.Reference, page.Reference, page.CriticalTokens);
            return page with { ImagePath = image };
        }).ToArray();
        return manifest with { Pages = pages };
    }

    // Match exact occurrences; a token embedded in a longer identifier is not recovered.
    private static int Count(string text, string token) => Regex.Matches(text,
        @"(?<![\p{L}\p{M}\p{N}_])" + Regex.Escape(token) + @"(?![\p{L}\p{M}\p{N}_])",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Count;
}
