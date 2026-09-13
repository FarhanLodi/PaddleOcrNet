namespace PaddleOcrNet.Services;

/// <summary>
/// The decode-time settings a <see cref="PaddleOcrService"/> applies to encoded inputs (files, streams,
/// bytes): the decompression-bomb pixel guard, EXIF orientation and transparency flattening.
/// </summary>
/// <param name="MaxImagePixels">Per-frame pixel limit (0 disables the guard).</param>
/// <param name="ApplyExifOrientation">Rotate/flip per the EXIF Orientation tag after decoding.</param>
/// <param name="FlattenTransparency">Composite images with alpha onto an opaque background.</param>
internal sealed record ImageLoadSettings(long MaxImagePixels, bool ApplyExifOrientation, bool FlattenTransparency)
{
    /// <summary>The settings of a default <see cref="PaddleOcrServiceOptions"/>.</summary>
    public static ImageLoadSettings Default { get; } = From(new PaddleOcrServiceOptions());

    /// <summary>Captures the load settings of the given service options.</summary>
    public static ImageLoadSettings From(PaddleOcrServiceOptions options)
        => new(options.MaxImagePixels, options.ApplyExifOrientation, options.FlattenTransparency);
}
