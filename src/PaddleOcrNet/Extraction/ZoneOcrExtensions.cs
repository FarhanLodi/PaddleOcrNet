using System.Collections.ObjectModel;
using System.Diagnostics;
using PaddleOcrNet.Extraction.Internal;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace PaddleOcrNet.Extraction;

/// <summary>
/// Zone (template) OCR for <see cref="IPaddleOcrService"/>: read named rectangles of a fixed-layout form
/// (invoice number, date, total, …) in one call, each with its own mode and allowlist.
/// </summary>
public static class ZoneOcrExtensions
{
    /// <summary>
    /// Reads each zone of an already-decoded image in a single <see cref="OcrLanguage"/>. The caller keeps
    /// ownership of the image.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="image">The image to read.</param>
    /// <param name="zones">The zones, with unique names.</param>
    /// <param name="language">The recognition language. Defaults to <see cref="OcrLanguage.Auto"/>.</param>
    /// <param name="options">Base recognition options; each zone overrides the region and (when set) the allowlist.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The per-zone results in zone order.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">A zone is <c>null</c>, unnamed, or its name is duplicated.</exception>
    public static Task<ZoneOcrResult> RecognizeZonesAsync(
        this IPaddleOcrService service,
        Image<Rgb24> image,
        IReadOnlyList<OcrZone> zones,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => RecognizeZonesAsync(service, image, zones, new[] { language }, options, cancellationToken);

    /// <summary>
    /// Reads each zone of an already-decoded image across several <see cref="OcrLanguage"/> values. The
    /// caller keeps ownership of the image. Zones are read sequentially; a zone that resolves to an empty
    /// rectangle yields an empty result without calling the service.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="image">The image to read.</param>
    /// <param name="zones">The zones, with unique names.</param>
    /// <param name="languages">The recognition languages.</param>
    /// <param name="options">Base recognition options; each zone overrides the region and (when set) the allowlist.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The per-zone results in zone order.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">A zone is <c>null</c>, unnamed, or its name is duplicated.</exception>
    public static async Task<ZoneOcrResult> RecognizeZonesAsync(
        this IPaddleOcrService service,
        Image<Rgb24> image,
        IReadOnlyList<OcrZone> zones,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(languages);
        ValidateZones(zones);

        var baseOptions = options ?? RecognitionOptions.Default;
        var stopwatch = Stopwatch.StartNew();
        var results = new OrderedDictionary<string, OcrZoneResult>(zones.Count, StringComparer.Ordinal);
        foreach (var zone in zones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zoneResult = await RecognizeZoneAsync(service, image, zone, languages, baseOptions, cancellationToken).ConfigureAwait(false);
            results.Add(zone.Name, zoneResult);
        }

        return new ZoneOcrResult
        {
            Zones = new ReadOnlyDictionary<string, OcrZoneResult>(results),
            Duration = stopwatch.Elapsed,
            SourceWidth = image.Width,
            SourceHeight = image.Height,
        };
    }

    /// <summary>
    /// Reads each zone of an image file in a single <see cref="OcrLanguage"/>.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="zones">The zones, with unique names.</param>
    /// <param name="language">The recognition language. Defaults to <see cref="OcrLanguage.Auto"/>.</param>
    /// <param name="options">Base recognition options; each zone overrides the region and (when set) the allowlist.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The per-zone results in zone order.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The path is blank, or a zone is <c>null</c>, unnamed, or duplicated.</exception>
    /// <exception cref="FileNotFoundException">The image file does not exist.</exception>
    public static async Task<ZoneOcrResult> RecognizeZonesAsync(
        this IPaddleOcrService service,
        string imagePath,
        IReadOnlyList<OcrZone> zones,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ValidateZones(zones);

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);

        using var image = await Image.LoadAsync<Rgb24>(fullPath, cancellationToken).ConfigureAwait(false);
        return await RecognizeZonesAsync(service, image, zones, new[] { language }, options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OcrZoneResult> RecognizeZoneAsync(
        IPaddleOcrService service,
        Image<Rgb24> image,
        OcrZone zone,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions baseOptions,
        CancellationToken cancellationToken)
    {
        var allowlist = zone.Allowlist ?? baseOptions.Allowlist;
        var (x, y, width, height) = zone.Region.Resolve(image.Width, image.Height);

        OcrResult result;
        if (width < 2 || height < 2)
        {
            result = OcrResult.Empty with { SourceWidth = image.Width, SourceHeight = image.Height };
        }
        else if (zone.Mode == ZoneMode.SingleLine)
        {
            IReadOnlyList<OcrPoint> polygon = new OcrPoint[]
            {
                new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height),
            };
            var zoneOptions = baseOptions with { Region = null, Allowlist = allowlist };
            result = await service.RecognizeRegionsAsync(image, new[] { polygon }, languages, zoneOptions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var zoneOptions = baseOptions with { Region = zone.Region, Allowlist = allowlist };
            result = await service.ExtractTextFromImage(image, languages, zoneOptions, cancellationToken).ConfigureAwait(false);
        }

        var lines = result.Lines;
        string text = zone.Mode == ZoneMode.SingleLine
            ? string.Join(' ', lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0))
            : TextGeometry.JoinReadingOrder(lines);

        return new OcrZoneResult
        {
            Name = zone.Name,
            Zone = zone,
            Text = text,
            Confidence = TextGeometry.WeightedConfidence(lines),
            Lines = lines,
            Result = result,
        };
    }

    private static void ValidateZones(IReadOnlyList<OcrZone> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var zone in zones)
        {
            if (zone is null)
                throw new ArgumentException("Zones must not contain null entries.", nameof(zones));
            if (string.IsNullOrWhiteSpace(zone.Name))
                throw new ArgumentException("Every zone needs a non-blank name.", nameof(zones));
            if (!names.Add(zone.Name))
                throw new ArgumentException($"Duplicate zone name '{zone.Name}'.", nameof(zones));
            if (!Enum.IsDefined(zone.Mode))
                throw new ArgumentException($"Zone '{zone.Name}' has an unknown mode '{zone.Mode}'.", nameof(zones));
        }
    }
}
