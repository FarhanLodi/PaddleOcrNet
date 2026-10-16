using System.Text.Json;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Structure;

/// <summary>A page of a document, with original page membership and optional unmatched OCR evidence.</summary>
public sealed record DocumentPage
{
    /// <summary>Original one-based page number; gaps are allowed.</summary>
    public required int PageNumber { get; init; }
    /// <summary>The unchanged page analysis, including all original blocks.</summary>
    public required StructureResult Structure { get; init; }
    /// <summary>Text not assigned to a block. Assembly merges these with <see cref="StructureResult.OrphanLines"/>,
    /// deduplicating equivalent evidence without silently assigning it to a block.</summary>
    public IReadOnlyList<OcrLine> OrphanLines { get; init; } = Array.Empty<OcrLine>();
}

/// <summary>Stable reference to a block by original page number and index in the original block collection.</summary>
public readonly record struct DocumentBlockReference(int PageNumber, int BlockIndex);

/// <summary>Kind of evidence-supported relationship between original blocks.</summary>
public enum DocumentRelationshipKind
{
    /// <summary>Existing reading-order sequence within a page.</summary>
    Sequence,
    /// <summary>Strict geometric containment, without inferred semantic ownership.</summary>
    Contains,
    /// <summary>Matching header text and normalized placement on different pages.</summary>
    RepeatedHeader,
    /// <summary>Adjacent-page table edges and column counts suggest continuation; review is required.</summary>
    TableContinuationCandidate,
}

/// <summary>An explicit relationship; raw blocks remain separate even for strong candidates.</summary>
public sealed record DocumentRelationship
{
    /// <summary>Relationship type.</summary>
    public required DocumentRelationshipKind Kind { get; init; }
    /// <summary>Origin block.</summary>
    public required DocumentBlockReference From { get; init; }
    /// <summary>Destination block.</summary>
    public required DocumentBlockReference To { get; init; }
    /// <summary>Observable evidence, not a calibrated confidence.</summary>
    public required string Evidence { get; init; }
    /// <summary>Whether the interpretation needs review before semantic joining or omission.</summary>
    public bool RequiresReview { get; init; }
}

/// <summary>Multi-page structure preserving original blocks and orphan text, with explicit relationships.</summary>
public sealed record DocumentStructure
{
    /// <summary>Pages sorted by original page number, each retaining original block order and contents.</summary>
    public required IReadOnlyList<DocumentPage> Pages { get; init; }
    /// <summary>Geometric and semantic evidence between page blocks.</summary>
    public required IReadOnlyList<DocumentRelationship> Relationships { get; init; }
    /// <summary>Serialize full evidence using source generation, without trimming raw OCR lines.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, StructureJsonContext.Unescaped.DocumentStructure);
}
