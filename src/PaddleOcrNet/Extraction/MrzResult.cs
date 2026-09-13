namespace PaddleOcrNet.Extraction;

/// <summary>
/// The fields decoded from a travel document's machine-readable zone by <see cref="MrzParser"/>. Field
/// values have filler characters (<c>&lt;</c>) removed and positional OCR corrections applied; the
/// lines as located are kept in <see cref="RawLines"/>.
/// </summary>
public sealed record MrzResult
{
    /// <summary>The MRZ layout (TD1, TD2 or TD3).</summary>
    public MrzFormat Format { get; init; }

    /// <summary>The document code, e.g. <c>P</c> (passport), <c>I</c> or <c>ID</c> (identity card), <c>V</c> (visa).</summary>
    public required string DocumentType { get; init; }

    /// <summary>The issuing state or organization (ICAO three-letter code, e.g. <c>UTO</c>; Germany is <c>D</c>).</summary>
    public required string IssuingCountry { get; init; }

    /// <summary>The primary identifier (surname), words separated by spaces.</summary>
    public required string Surname { get; init; }

    /// <summary>The secondary identifier (given names), words separated by spaces; empty when absent.</summary>
    public required string GivenNames { get; init; }

    /// <summary>The document number (including a TD1 long-number extension when present).</summary>
    public required string DocumentNumber { get; init; }

    /// <summary>The holder's nationality (ICAO three-letter code).</summary>
    public required string Nationality { get; init; }

    /// <summary>The date of birth, or <c>null</c> when unreadable. Two-digit years resolve to the most recent past year.</summary>
    public DateOnly? BirthDate { get; init; }

    /// <summary>The sex field: <c>M</c>, <c>F</c>, <c>X</c>, or <c>&lt;</c> (unspecified).</summary>
    public required string Sex { get; init; }

    /// <summary>The date of expiry, or <c>null</c> when unreadable. Two-digit years resolve to 2000–2099 unless more than 50 years ahead.</summary>
    public DateOnly? ExpiryDate { get; init; }

    /// <summary>The optional data element (personal number on TD3; line-1 optional data on TD1).</summary>
    public required string OptionalData { get; init; }

    /// <summary>The second optional data element (TD1 line 2 only); <c>null</c> for other formats.</summary>
    public string? OptionalData2 { get; init; }

    /// <summary>The per-field check-digit outcomes.</summary>
    public required MrzCheckResults Checks { get; init; }

    /// <summary>True when every applicable check digit, including the composite, matches.</summary>
    public bool IsValid => Checks.AllPassed;

    /// <summary>The MRZ lines as located and normalized (spaces removed, fillers restored), before positional corrections.</summary>
    public required IReadOnlyList<string> RawLines { get; init; }
}
