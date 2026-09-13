using PaddleOcrNet.Models;

namespace PaddleOcrNet.Extraction;

/// <summary>
/// A named field of a fixed-layout form or template, read by
/// <see cref="ZoneOcrExtensions.RecognizeZonesAsync(PaddleOcrNet.Services.IPaddleOcrService, EasyImageSharp.Image{EasyImageSharp.PixelFormats.Rgb24}, IReadOnlyList{OcrZone}, OcrLanguage, RecognitionOptions?, CancellationToken)"/>.
/// </summary>
/// <param name="Name">Unique name of the zone; the key in <see cref="ZoneOcrResult.Zones"/>.</param>
/// <param name="Region">The zone rectangle, in pixels (<see cref="OcrRegion.Pixels"/>) or as fractions of the image size (<see cref="OcrRegion.Fraction"/>).</param>
/// <param name="Mode">How the zone is read. Defaults to <see cref="ZoneMode.Detect"/>.</param>
/// <param name="Allowlist">
/// Optional per-zone recognizer allowlist (same shape as <see cref="RecognitionOptions.Allowlist"/>, e.g.
/// <c>RecognitionOptions.FromCharacters("0123456789.,")</c>). <c>null</c> keeps the call's options.
/// </param>
public sealed record OcrZone(
    string Name,
    OcrRegion Region,
    ZoneMode Mode = ZoneMode.Detect,
    IReadOnlyCollection<string>? Allowlist = null);
