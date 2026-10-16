namespace PaddleOcrNet.Models;

/// <summary>Opt-in controls for automatic recognizer selection. Scores remain raw in OCR lines.</summary>
public sealed record AutoRoutingOptions
{
    /// <summary>Maximum candidate packs to evaluate, in caller candidate order. Zero evaluates all.</summary>
    public int MaxCandidatePacks { get; init; }
    /// <summary>Disable the page-average shortcut so minority-script crops see every admitted candidate. Default true.</summary>
    public bool EvaluateAllCandidates { get; init; } = true;
    /// <summary>Externally fitted calibration for every admitted pack, keyed by pack name. Empty uses raw scores.</summary>
    public IReadOnlyDictionary<string, RecognitionCalibration> Calibration { get; init; }
        = new Dictionary<string, RecognitionCalibration>();
}

/// <summary>Monotonic correctness calibration tied to the exact model and dictionary registry hashes.</summary>
public sealed record RecognitionCalibration
{
    /// <summary>Calibration dataset/version identifier, independent of held-out evaluation data.</summary>
    public required string Version { get; init; }
    /// <summary>SHA-256 of the model used to fit this curve.</summary>
    public required string ModelSha256 { get; init; }
    /// <summary>SHA-256 of the dictionary used to fit this curve.</summary>
    public required string DictionarySha256 { get; init; }
    /// <summary>Strictly increasing raw-confidence knots covering 0 and 1.</summary>
    public required IReadOnlyList<double> RawConfidence { get; init; }
    /// <summary>Nondecreasing fitted correctness probabilities corresponding to the knots.</summary>
    public required IReadOnlyList<double> Correctness { get; init; }

    internal void Validate(string? modelHash, string? dictionaryHash)
    {
        if (string.IsNullOrWhiteSpace(Version) || string.IsNullOrWhiteSpace(modelHash)
            || string.IsNullOrWhiteSpace(dictionaryHash)
            || !string.Equals(ModelSha256, modelHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(DictionarySha256, dictionaryHash, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Calibration version and model/dictionary hashes must match the recognizer assets.");
        if (RawConfidence is null || Correctness is null || RawConfidence.Count < 2
            || RawConfidence.Count != Correctness.Count || RawConfidence[0] != 0 || RawConfidence[^1] != 1)
            throw new ArgumentException("Calibration requires matching knot arrays spanning raw confidence 0 through 1.");
        for (int i = 0; i < RawConfidence.Count; i++)
            if (!double.IsFinite(RawConfidence[i]) || !double.IsFinite(Correctness[i])
                || RawConfidence[i] is < 0 or > 1 || Correctness[i] is < 0 or > 1
                || (i > 0 && (RawConfidence[i] <= RawConfidence[i - 1] || Correctness[i] < Correctness[i - 1])))
                throw new ArgumentException("Calibration must contain finite monotonic knots and probabilities in [0,1].");
    }

    internal double Map(double raw)
    {
        if (!double.IsFinite(raw)) return 0;
        raw = Math.Clamp(raw, 0, 1);
        if (raw == 0) return Correctness[0];
        if (raw == 1) return Correctness[^1];
        for (int i = 1; i < RawConfidence.Count; i++)
            if (raw <= RawConfidence[i])
                return Correctness[i - 1] + (Correctness[i] - Correctness[i - 1])
                    * (raw - RawConfidence[i - 1]) / (RawConfidence[i] - RawConfidence[i - 1]);
        return Correctness[^1];
    }
}
