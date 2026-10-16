using PaddleOcrNet.Models;

namespace PaddleOcrNet.Tests.Evaluation;

internal sealed record CalibrationSample(string DocumentId, string Pack, string Split, double RawConfidence, bool Correct);
internal sealed record CalibrationAssets(string ModelSha256, string DictionarySha256);
internal sealed record CalibrationFit(IReadOnlyDictionary<string, RecognitionCalibration> Curves, IReadOnlySet<string> FittingDocuments);
internal sealed record ReliabilityBin(int Count, double MeanConfidence, double Accuracy);
internal sealed record RiskCoverage(double Threshold, double Coverage, double ErrorRate);
internal sealed record CalibrationEvaluation(int Samples, double ExpectedCalibrationError, double BrierScore,
    IReadOnlyList<ReliabilityBin> Reliability, IReadOnlyList<RiskCoverage> RiskCoverage);

/// <summary>Offline supervised fitting; none of these routines manufacture labels from OCR scores.</summary>
internal static class ConfidenceCalibration
{
    internal static CalibrationFit Fit(IReadOnlyList<CalibrationSample> samples,
        IReadOnlyDictionary<string, CalibrationAssets> assets, string version)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("A dataset/version is required.");
        ValidateSamples(samples, "calibration");
        var curves = new Dictionary<string, RecognitionCalibration>(StringComparer.Ordinal);
        foreach (var pack in samples.GroupBy(s => s.Pack))
        {
            if (!assets.TryGetValue(pack.Key, out var hashes) || !IsHash(hashes.ModelSha256) || !IsHash(hashes.DictionarySha256))
                throw new ArgumentException($"Pack {pack.Key} requires exact model and dictionary SHA-256 values.");
            var groups = pack.GroupBy(s => s.RawConfidence).OrderBy(g => g.Key)
                .Select(g => (Score: g.Key, Count: g.Count(), Correct: g.Count(s => s.Correct))).ToArray();
            // Pool adjacent violators with observation-count weighting, including tied confidence scores.
            var blocks = new List<(int Start, int End, int Count, int Correct)>();
            for (int i = 0; i < groups.Length; i++)
            {
                blocks.Add((i, i, groups[i].Count, groups[i].Correct));
                while (blocks.Count > 1)
                {
                    var a = blocks[^2];
                    var b = blocks[^1];
                    if (a.Correct / (double)a.Count <= b.Correct / (double)b.Count) break;
                    blocks.RemoveRange(blocks.Count - 2, 2);
                    blocks.Add((a.Start, b.End, a.Count + b.Count, a.Correct + b.Correct));
                }
            }
            var fitted = new double[groups.Length];
            foreach (var block in blocks)
                for (int i = block.Start; i <= block.End; i++) fitted[i] = block.Correct / (double)block.Count;
            var x = groups.Select(g => g.Score).ToList();
            var y = fitted.ToList();
            if (x[0] > 0) { x.Insert(0, 0); y.Insert(0, y[0]); }
            if (x[^1] < 1) { x.Add(1); y.Add(y[^1]); }
            var curve = new RecognitionCalibration
            {
                Version = version, ModelSha256 = hashes.ModelSha256, DictionarySha256 = hashes.DictionarySha256,
                RawConfidence = x, Correctness = y,
            };
            curve.Validate(hashes.ModelSha256, hashes.DictionarySha256);
            curves.Add(pack.Key, curve);
        }
        return new CalibrationFit(curves, samples.Select(s => s.DocumentId).ToHashSet(StringComparer.Ordinal));
    }

    internal static CalibrationEvaluation Evaluate(CalibrationFit fit, IReadOnlyList<CalibrationSample> heldOut, int binCount = 10)
    {
        ValidateSamples(heldOut, "test");
        if (binCount < 1 || binCount > 1000) throw new ArgumentOutOfRangeException(nameof(binCount));
        if (heldOut.Any(s => fit.FittingDocuments.Contains(s.DocumentId)))
            throw new ArgumentException("A held-out document was used during calibration.");
        var predictions = heldOut.Select(s =>
        {
            if (!fit.Curves.TryGetValue(s.Pack, out var curve))
                throw new ArgumentException($"No calibrated curve for pack {s.Pack}.");
            return (Score: curve.Map(s.RawConfidence), Correct: s.Correct ? 1d : 0d);
        }).ToArray();
        var bins = new List<ReliabilityBin>();
        double ece = 0;
        for (int b = 0; b < binCount; b++)
        {
            var bucket = predictions.Where(p => Math.Min(binCount - 1, (int)(p.Score * binCount)) == b).ToArray();
            double mean = bucket.Length == 0 ? 0 : bucket.Average(p => p.Score);
            double accuracy = bucket.Length == 0 ? 0 : bucket.Average(p => p.Correct);
            bins.Add(new ReliabilityBin(bucket.Length, mean, accuracy));
            ece += bucket.Length / (double)predictions.Length * Math.Abs(mean - accuracy);
        }
        // Include complete tied-score groups: accepting half a tie would invent an unavailable policy.
        int accepted = 0;
        double errors = 0;
        var risk = new List<RiskCoverage>();
        foreach (var group in predictions.GroupBy(p => p.Score).OrderByDescending(g => g.Key))
        {
            accepted += group.Count();
            errors += group.Sum(p => 1 - p.Correct);
            risk.Add(new RiskCoverage(group.Key, accepted / (double)predictions.Length, errors / accepted));
        }
        return new CalibrationEvaluation(predictions.Length, ece,
            predictions.Average(p => Math.Pow(p.Score - p.Correct, 2)), bins, risk);
    }

    private static void ValidateSamples(IReadOnlyList<CalibrationSample> samples, string split)
    {
        if (samples.Count == 0) throw new ArgumentException("Labels must not be empty.");
        foreach (var sample in samples)
            if (string.IsNullOrWhiteSpace(sample.DocumentId) || string.IsNullOrWhiteSpace(sample.Pack)
                || sample.Split != split || !double.IsFinite(sample.RawConfidence) || sample.RawConfidence is < 0 or > 1)
                throw new ArgumentException($"Samples require document/pack IDs, {split} split, and finite confidence in [0,1].");
    }

    private static bool IsHash(string hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}
