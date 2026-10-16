namespace PaddleOcrNet.Models;

/// <summary>Opt-in recovery effort; these names do not promise measured accuracy.</summary>
public enum DocumentProcessingProfile
{
    /// <summary>Assess the original result without additional inference.</summary>
    Fast,
    /// <summary>Allow at most one selective recognition call by default.</summary>
    Balanced,
    /// <summary>Allow at most two selective recognition calls by default.</summary>
    Accurate
}

/// <summary>Admission limits for optional recovery after ordinary OCR. Original OCR is never suppressed.</summary>
public sealed record DocumentProcessingPolicy
{
    /// <summary>Default recovery call limit when MaximumExtraPasses is not supplied.</summary>
    public DocumentProcessingProfile Profile { get; init; } = DocumentProcessingProfile.Balanced;
    /// <summary>Maximum extra service calls; null uses the profile limit. Weak-line recovery uses one call per line; a rescan uses one detection call plus one batched recognition call.</summary>
    public int? MaximumExtraPasses { get; init; }
    /// <summary>Conservative extra input-pixel budget: each recovery call charges the full source image area. This is not a tensor-memory limit.</summary>
    public long MaximumExtraPixels { get; init; } = 40_000_000;
    /// <summary>Total elapsed admission budget including original OCR. Cancellation during native inference remains cooperative.</summary>
    public TimeSpan MaximumElapsed { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Raw-score review heuristic, not a calibrated correctness probability.</summary>
    public double ReviewBelowConfidence { get; init; } = 0.85;
    /// <summary>Explicit source-frame recognition settings. Disable UseDocOrientation, UseDocUnwarp and every Preprocessing flag, including CorrectNonSquarePixels. If original options were supplied, weak-line recovery requires this opt-in.</summary>
    public RecognitionOptions? RecoveryOptions { get; init; }
    /// <summary>Opt in to a detector rescan when no text or small text was returned. New readings remain separate, unverified candidates.</summary>
    public bool EnableDetectionRescan { get; init; }
    /// <summary>Explicit alternate detector settings; required for rescanning when original recognition options were supplied.</summary>
    public DetectionOptions? DetectionRescanOptions { get; init; }
    /// <summary>Maximum newly detected regions admitted together to recognition.</summary>
    public int MaximumRescanRegions { get; init; } = 32;
    /// <summary>Returned line height in source pixels below which a rescan is considered.</summary>
    public double SmallTextHeight { get; init; } = 16;

    internal int Validate()
    {
        if (RecoveryOptions is { } recovery && (recovery.UseDocOrientation || recovery.UseDocUnwarp
            || recovery.Preprocessing.Deskew || recovery.Preprocessing.Denoise || recovery.Preprocessing.Binarize
            || recovery.Preprocessing.DetectOrientation || recovery.Preprocessing.CorrectNonSquarePixels))
            throw new ArgumentException("RecoveryOptions must use source-frame recognition: set UseDocOrientation=false, UseDocUnwarp=false, and all Preprocessing flags false, including CorrectNonSquarePixels. Page preprocessing applies only to the initial full-page OCR call.", nameof(RecoveryOptions));
        if (!Enum.IsDefined(Profile)) throw new ArgumentOutOfRangeException(nameof(Profile));
        if (MaximumExtraPasses < 0) throw new ArgumentOutOfRangeException(nameof(MaximumExtraPasses));
        if (MaximumExtraPixels < 0) throw new ArgumentOutOfRangeException(nameof(MaximumExtraPixels));
        if (MaximumRescanRegions <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumRescanRegions));
        if (!double.IsFinite(SmallTextHeight) || SmallTextHeight <= 0) throw new ArgumentOutOfRangeException(nameof(SmallTextHeight));
        if (MaximumElapsed <= TimeSpan.Zero || MaximumElapsed.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumElapsed));
        if (!double.IsFinite(ReviewBelowConfidence) || ReviewBelowConfidence < 0 || ReviewBelowConfidence > 1)
            throw new ArgumentOutOfRangeException(nameof(ReviewBelowConfidence));
        return MaximumExtraPasses ?? (int)Profile;
    }
}

/// <summary>An auditable quality assessment. Raw model confidence is not calibrated accuracy.</summary>
public sealed record PageQualityReport
{
    /// <summary>Sampled image diagnostics. These do not establish blur, illumination defects or OCR correctness.</summary>
    public PageImageQuality ImageQuality { get; init; }
    /// <summary>Whether unresolved evidence warrants human review.</summary>
    public bool RequiresReview { get; init; }
    /// <summary>Stable reason codes for unresolved page evidence.</summary>
    public IReadOnlyList<string> ReviewReasons { get; init; } = Array.Empty<string>();
    /// <summary>True when the configured recovery budget prevented or interrupted additional work.</summary>
    public bool BudgetExhausted { get; init; }
    /// <summary>Number of admitted recovery calls, including a call interrupted by the elapsed limit.</summary>
    public int ExtraPasses { get; init; }
    /// <summary>Conservatively charged extra source pixels, not inference tensor pixels.</summary>
    public long ExtraPixels { get; init; }
    /// <summary>Total measured wall-clock duration including baseline recognition.</summary>
    public TimeSpan Elapsed { get; init; }
    /// <summary>False: this deterministic assessment contains no learned confidence calibration.</summary>
    public bool IsConfidenceCalibrated { get; init; }
}

/// <summary>Original line and independently retained recovery candidates; no automatic text replacement.</summary>
public sealed record RegionEvidence
{
    /// <summary>Zero-based index in the raw result.</summary>
    public int LineIndex { get; init; }
    /// <summary>Original recognized line and geometry.</summary>
    public required OcrLine Original { get; init; }
    /// <summary>All returned alternative readings, including disagreements and empty candidates.</summary>
    public IReadOnlyList<OcrLine> Alternatives { get; init; } = Array.Empty<OcrLine>();
    /// <summary>Stable reason codes. Agreement alone does not establish correctness.</summary>
    public IReadOnlyList<string> ReviewReasons { get; init; } = Array.Empty<string>();
}

/// <summary>Ordinary OCR plus separately auditable adaptive processing evidence.</summary>
public sealed record DocumentProcessingResult
{
    /// <summary>New readings from a detector rescan, never silently merged into the original transcription.</summary>
    public IReadOnlyList<OcrLine> AdditionalCandidates { get; init; } = Array.Empty<OcrLine>();
    /// <summary>New detector regions without a matching returned reading, including failed/empty recognition attempts and regions excluded by admission limits.</summary>
    public IReadOnlyList<DetectedRegion> UnreadRegions { get; init; } = Array.Empty<DetectedRegion>();
    /// <summary>Original unmodified service result.</summary>
    public required OcrResult RawResult { get; init; }
    /// <summary>Page review and resource accounting.</summary>
    public required PageQualityReport Quality { get; init; }
    /// <summary>Evidence for every returned original line, in original order.</summary>
    public IReadOnlyList<RegionEvidence> Regions { get; init; } = Array.Empty<RegionEvidence>();
}
