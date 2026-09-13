namespace PaddleOcrNet.Extraction;

/// <summary>
/// The kind of value an <see cref="OcrMatch"/> represents. The built-in kinds correspond to the
/// patterns in <see cref="OcrPatterns"/>.
/// </summary>
public enum OcrMatchKind
{
    /// <summary>A match from a caller-supplied <see cref="System.Text.RegularExpressions.Regex"/> or <see cref="OcrPattern"/>.</summary>
    Custom = 0,

    /// <summary>An e-mail address (<see cref="OcrPatterns.Email"/>).</summary>
    Email,

    /// <summary>A web URL (<see cref="OcrPatterns.Url"/>).</summary>
    Url,

    /// <summary>A telephone number (<see cref="OcrPatterns.Phone"/>).</summary>
    Phone,

    /// <summary>A calendar date (<see cref="OcrPatterns.Date"/>).</summary>
    Date,

    /// <summary>A monetary amount with a currency symbol or ISO 4217 code (<see cref="OcrPatterns.Amount"/>).</summary>
    Amount,

    /// <summary>An IBAN that passed its mod-97 check (<see cref="OcrPatterns.Iban"/>).</summary>
    Iban,

    /// <summary>A payment card number that passed the Luhn check (<see cref="OcrPatterns.PaymentCard"/>).</summary>
    PaymentCard,

    /// <summary>A percentage (<see cref="OcrPatterns.Percentage"/>).</summary>
    Percentage,
}
