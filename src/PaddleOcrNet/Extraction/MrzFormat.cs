namespace PaddleOcrNet.Extraction;

/// <summary>
/// The ICAO Doc 9303 machine-readable-zone layout of a travel document.
/// </summary>
public enum MrzFormat
{
    /// <summary>TD1: three lines of 30 characters (ID cards).</summary>
    Td1,

    /// <summary>TD2: two lines of 36 characters (older ID cards; MRV-B visas share the size).</summary>
    Td2,

    /// <summary>TD3: two lines of 44 characters (passports; MRV-A visas share the size).</summary>
    Td3,
}
