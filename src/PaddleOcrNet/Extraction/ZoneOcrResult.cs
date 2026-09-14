namespace PaddleOcrNet.Extraction;

/// <summary>
/// The result of reading a set of <see cref="OcrZone"/>s from one image.
/// </summary>
public sealed record ZoneOcrResult
{
    /// <summary>
    /// The per-zone results keyed by zone name (ordinal), enumerated in the order the zones were supplied.
    /// </summary>
    public required IReadOnlyDictionary<string, OcrZoneResult> Zones { get; init; }

    /// <summary>Gets the result for the zone named <paramref name="name"/>.</summary>
    /// <param name="name">The zone name.</param>
    /// <exception cref="KeyNotFoundException">No zone has that name.</exception>
    public OcrZoneResult this[string name] => Zones[name];

    /// <summary>Total time spent reading all zones.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Width (px) of the image the zones were read from.</summary>
    public int SourceWidth { get; init; }

    /// <summary>Height (px) of the image the zones were read from.</summary>
    public int SourceHeight { get; init; }
}
