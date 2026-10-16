using System.Text.Json;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Structure;

/// <summary>Explicit document relationship to verify, without inferring a schema.</summary>
public enum DocumentVerificationKind
{
    /// <summary>Every input block must have exactly the same ordinal text as the expected block.</summary>
    ExactText,
    /// <summary>The sum of complete decimal input texts must equal the expected block's decimal text within tolerance.</summary>
    DecimalSum,
}

/// <summary>A caller-bound relationship. Text is read directly from original blocks, never extracted from prose.</summary>
public sealed record DocumentVerificationRule
{
    /// <summary>Unique caller-supplied rule identifier.</summary>
    public required string Id { get; init; }
    /// <summary>Comparison to perform.</summary>
    public required DocumentVerificationKind Kind { get; init; }
    /// <summary>Operand or repeated-value block references.</summary>
    public required IReadOnlyList<DocumentBlockReference> Inputs { get; init; }
    /// <summary>Total or reference-value block.</summary>
    public required DocumentBlockReference Expected { get; init; }
    /// <summary>Explicit numeric culture; empty means invariant. Group separators, currencies and exponents are not accepted.</summary>
    public string CultureName { get; init; } = "";
    /// <summary>Nonnegative absolute tolerance for DecimalSum, in the same units as the input values.</summary>
    public decimal AbsoluteTolerance { get; init; }
}

/// <summary>Hard admission limits for deterministic relationship verification and requested rereads.</summary>
public sealed record DocumentVerificationPolicy
{
    /// <summary>Maximum admitted rules. Defaults to 64; allowed range 1..10000.</summary>
    public int MaxRules { get; init; } = 64;
    /// <summary>Total admitted operand/expected references across rules. Defaults to 256; range 1..100000.</summary>
    public int MaxReferences { get; init; } = 256;
    /// <summary>Maximum unique reread regions. Defaults to 16; range 0..10000.</summary>
    public int MaxRereadRequests { get; init; } = 16;
    /// <summary>Total source pixels admitted for reread requests. Defaults to 16 million; zero disables requests.</summary>
    public long MaxRereadPixels { get; init; } = 16_000_000;
}

/// <summary>Outcome of one explicit relationship check, not an accuracy probability.</summary>
public enum DocumentVerificationStatus
{
    /// <summary>The supplied readings satisfy the rule; this does not prove OCR correctness.</summary>
    Consistent,
    /// <summary>The readings disagree; the printed document itself may be inconsistent.</summary>
    Contradiction,
    /// <summary>Missing, empty or nondecimal readings, or arithmetic overflow, prevent comparison.</summary>
    Unverifiable,
    /// <summary>The reference budget prevented admission.</summary>
    NotEvaluated,
}

/// <summary>Original block evidence used by a check.</summary>
public sealed record DocumentVerificationReading
{
    /// <summary>Original page and block index.</summary>
    public required DocumentBlockReference Source { get; init; }
    /// <summary>Unmodified block text, or null for a missing/unreadable source.</summary>
    public string? RawText { get; init; }
    /// <summary>Original block bounds in its declared coordinate frame.</summary>
    public OcrBoundingBox Bounds { get; init; }
    /// <summary>Original declared coordinate history, retained even when a source reread cannot be scheduled.</summary>
    public StructureCoordinateProvenance? Coordinates { get; init; }
    /// <summary>Whether a matching source block exists.</summary>
    public bool Found { get; init; }
}

/// <summary>Rule outcome and unmodified readings, including the expected reading as the final entry.</summary>
public sealed record DocumentVerificationCheck
{
    /// <summary>Caller rule identifier.</summary>
    public required string RuleId { get; init; }
    /// <summary>Comparison outcome.</summary>
    public required DocumentVerificationStatus Status { get; init; }
    /// <summary>Machine-readable reason; never an instruction to alter a value.</summary>
    public required string Reason { get; init; }
    /// <summary>Original operands followed by the expected value.</summary>
    public IReadOnlyList<DocumentVerificationReading> Readings { get; init; } = Array.Empty<DocumentVerificationReading>();
}

/// <summary>Explicit request for a caller to reread an original-source region; no OCR is executed by verification.</summary>
public sealed record DocumentRereadRequest
{
    /// <summary>Original page and block to review.</summary>
    public required DocumentBlockReference Source { get; init; }
    /// <summary>Integer-covering rectangle in original source pixels.</summary>
    public required OcrBoundingBox Bounds { get; init; }
    /// <summary>First failing rule requesting this region.</summary>
    public required string RuleId { get; init; }
}

/// <summary>Audit trail for a region that could not be admitted for rereading.</summary>
public sealed record DocumentRereadSkip(DocumentBlockReference Source, string RuleId, string Reason);

/// <summary>Bounded verification evidence. Raw document structures are never rewritten.</summary>
public sealed record DocumentVerificationReport
{
    /// <summary>Admitted rule results, including checks skipped by the reference budget.</summary>
    public required IReadOnlyList<DocumentVerificationCheck> Checks { get; init; }
    /// <summary>Unique source regions admitted for optional rereading.</summary>
    public required IReadOnlyList<DocumentRereadRequest> RequestedRereads { get; init; }
    /// <summary>Reasons unavailable or over-budget regions were not scheduled.</summary>
    public required IReadOnlyList<DocumentRereadSkip> SkippedRereads { get; init; }
    /// <summary>True if the enumerable contained more rules than MaxRules.</summary>
    public bool RulesTruncated { get; init; }
    /// <summary>Sum of integer-covering rectangle areas admitted for rereading.</summary>
    public long RequestedPixels { get; init; }
    /// <summary>Serialize evidence with source generation.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, StructureJsonContext.Unescaped.DocumentVerificationReport);
}
