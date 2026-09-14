namespace PaddleOcrNet.Extraction;

/// <summary>
/// Per-field ICAO 9303 check-digit outcomes for an <see cref="MrzResult"/>. <c>null</c> means the
/// document format has no such check digit.
/// </summary>
/// <param name="DocumentNumber">Whether the document-number check digit matches.</param>
/// <param name="BirthDate">Whether the date-of-birth check digit matches.</param>
/// <param name="ExpiryDate">Whether the date-of-expiry check digit matches.</param>
/// <param name="OptionalData">Whether the personal-number check digit matches (TD3 passports only).</param>
/// <param name="Composite">Whether the composite check digit matches (absent on MRV visas).</param>
public sealed record MrzCheckResults(
    bool DocumentNumber,
    bool BirthDate,
    bool ExpiryDate,
    bool? OptionalData,
    bool? Composite)
{
    /// <summary>True when every applicable check digit matches.</summary>
    public bool AllPassed => DocumentNumber && BirthDate && ExpiryDate && OptionalData != false && Composite != false;

    /// <summary>Number of applicable check digits that match.</summary>
    internal int PassedCount =>
        (DocumentNumber ? 1 : 0) + (BirthDate ? 1 : 0) + (ExpiryDate ? 1 : 0)
        + (OptionalData == true ? 1 : 0) + (Composite == true ? 1 : 0);
}
