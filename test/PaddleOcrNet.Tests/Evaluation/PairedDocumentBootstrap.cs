namespace PaddleOcrNet.Tests.Evaluation;

internal sealed record PairedDocumentScore(string DocumentId, string Subgroup, double BaselineError, double CandidateError);
internal sealed record PairedErrorInterval(int Documents, double MeanDelta, double Lower95, double Upper95, string Gate);

/// <summary>Document-level paired macro-error bootstrap; negative deltas favor the candidate.</summary>
internal static class PairedDocumentBootstrap
{
    internal static IReadOnlyDictionary<string, PairedErrorInterval> Evaluate(IReadOnlyList<PairedDocumentScore> scores,
        int minimumDocuments = 20, int replicates = 2000, int seed = 1729, double allowedRegression = 0)
    {
        if (minimumDocuments < 2 || replicates < 100 || !double.IsFinite(allowedRegression) || allowedRegression < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumDocuments));
        if (scores.Count == 0 || scores.Any(s => string.IsNullOrWhiteSpace(s.DocumentId) || string.IsNullOrWhiteSpace(s.Subgroup)
            || !double.IsFinite(s.BaselineError) || !double.IsFinite(s.CandidateError) || s.BaselineError < 0 || s.CandidateError < 0))
            throw new ArgumentException("Finite nonnegative document errors and subgroup IDs are required.");
        if (scores.GroupBy(s => (s.DocumentId, s.Subgroup)).Any(g => g.Count() > 1))
            throw new ArgumentException("Aggregate correlated pages/crops to one score per document and subgroup before bootstrapping.");
        var output = new Dictionary<string, PairedErrorInterval>();
        foreach (var group in scores.GroupBy(s => s.Subgroup))
        {
            double[] delta = group.OrderBy(s => s.DocumentId, StringComparer.Ordinal).Select(s => s.CandidateError - s.BaselineError).ToArray();
            var random = new Random(seed);
            var means = new double[replicates];
            for (int r = 0; r < replicates; r++)
            {
                double sum = 0;
                for (int d = 0; d < delta.Length; d++) sum += delta[random.Next(delta.Length)];
                means[r] = sum / delta.Length;
            }
            Array.Sort(means);
            double lower = means[(int)Math.Floor((replicates - 1) * .025)];
            double upper = means[(int)Math.Ceiling((replicates - 1) * .975)];
            string gate = delta.Length < minimumDocuments ? "insufficient-evidence"
                : upper <= allowedRegression ? "pass" : lower > allowedRegression ? "regression" : "inconclusive";
            output.Add(group.Key, new PairedErrorInterval(delta.Length, delta.Average(), lower, upper, gate));
        }
        return output;
    }
}
