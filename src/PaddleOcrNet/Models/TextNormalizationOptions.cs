namespace PaddleOcrNet.Models;

/// <summary>Explicit presentation-only changes; none are enabled by default.</summary>
public sealed record TextNormalizationOptions
{
    /// <summary>Canonical Unicode NFC, preserving accents. Compatibility normalization is never performed.</summary>
    public bool CanonicalUnicode { get; init; }
    /// <summary>Collapse horizontal whitespace per line and trim line edges, retaining line breaks.</summary>
    public bool CleanWhitespace { get; init; }
    /// <summary>Join a letter-hyphen-newline-letter sequence. This can change meaning; opt in only for reviewed material.</summary>
    public bool JoinHyphenatedLines { get; init; }
    /// <summary>Explicit zero-based physical text-line indices to suppress, for example reviewed repeated headers. No header inference occurs.</summary>
    public IReadOnlyList<int> SuppressLineIndices { get; init; } = Array.Empty<int>();
}

/// <summary>A single reversible presentation change recorded in application order.</summary>
public sealed record TextTransformation
{
    /// <summary>Stable transformation name.</summary>
    public required string Kind { get; init; }
    /// <summary>Full text immediately before this transformation.</summary>
    public required string Before { get; init; }
    /// <summary>Full text immediately after this transformation.</summary>
    public required string After { get; init; }
}

/// <summary>Raw transcription alongside its requested presentation view and complete change log.</summary>
public sealed record NormalizedTextResult
{
    /// <summary>Unmodified original FullText.</summary>
    public required string RawText { get; init; }
    /// <summary>Explicitly transformed presentation text, not replacement OCR evidence.</summary>
    public required string Text { get; init; }
    /// <summary>Only transformations that actually changed the text, in application order.</summary>
    public IReadOnlyList<TextTransformation> Transformations { get; init; } = Array.Empty<TextTransformation>();
}
