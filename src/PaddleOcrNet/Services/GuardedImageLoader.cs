using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using PaddleOcrNet.Internal;

namespace PaddleOcrNet.Services;

/// <summary>
/// Decodes encoded inputs for the OCR service: the decompression-bomb pixel guard (checked from the
/// header before any pixel memory is allocated), single-frame decoding for the single-image APIs,
/// transparency flattening and EXIF orientation. Every encoded input is buffered once and decoded from
/// memory, so a file is read a single time for both the header check and the decode.
/// </summary>
internal static class GuardedImageLoader
{
    /// <summary>
    /// Decodes only the first frame of <paramref name="bytes"/> into an opaque, upright RGB image.
    /// </summary>
    internal static Image<Rgb24> LoadFirstFrame(ReadOnlySpan<byte> bytes, ImageLoadSettings settings)
    {
        var format = Inspect(bytes, settings);
        var decoder = CreateDecoderOptions(settings, maxFrames: 1);
        try
        {
            Image<Rgb24> image;
            if (settings.FlattenTransparency && MayHaveAlpha(format))
            {
                using var rgba = Image.Load<Rgba32>(bytes, decoder);
                image = TransparencyFlattener.Flatten(rgba);
            }
            else
            {
                image = Image.Load<Rgb24>(bytes, decoder);
            }
            return ApplyOrientation(image, settings);
        }
        catch (ImageSizeLimitExceededException ex)
        {
            throw TooLarge(ex);
        }
    }

    /// <summary>
    /// Decodes every frame of <paramref name="bytes"/>. Returns an <see cref="Image{Rgba32}"/> when the
    /// frames must still be flattened (see <see cref="TransparencyFlattener"/>), otherwise an
    /// <see cref="Image{Rgb24}"/>. Each frame is checked against the pixel limit before it is allocated.
    /// </summary>
    internal static Image LoadAllFrames(ReadOnlySpan<byte> bytes, ImageLoadSettings settings)
    {
        var format = Inspect(bytes, settings);
        var decoder = CreateDecoderOptions(settings, maxFrames: int.MaxValue);
        try
        {
            return settings.FlattenTransparency && MayHaveAlpha(format)
                ? Image.Load<Rgba32>(bytes, decoder)
                : Image.Load<Rgb24>(bytes, decoder);
        }
        catch (ImageSizeLimitExceededException ex)
        {
            throw TooLarge(ex);
        }
    }

    /// <summary>
    /// Applies the EXIF Orientation tag (2–8) in place when enabled, returning the same image.
    /// </summary>
    internal static Image<Rgb24> ApplyOrientation(Image<Rgb24> image, ImageLoadSettings settings)
    {
        if (settings.ApplyExifOrientation
            && image.Metadata.ExifProfile is { } exif
            && exif.TryGetValue(ExifTag.Orientation, out var orientation)
            && orientation.Value is >= 2 and <= 8)
        {
            image.Mutate(c => c.AutoOrient());
        }
        return image;
    }

    /// <summary>Reads a whole file into memory.</summary>
    internal static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        => File.ReadAllBytesAsync(path, cancellationToken);

    /// <summary>Reads the rest of a stream into memory.</summary>
    internal static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>
    /// Throws <see cref="ImageTooLargeException"/> when <paramref name="width"/> × <paramref name="height"/>
    /// exceeds <paramref name="maxImagePixels"/> (0 disables the check).
    /// </summary>
    internal static void GuardPixels(int width, int height, long maxImagePixels)
    {
        long pixels = (long)width * height;
        if (maxImagePixels > 0 && pixels > maxImagePixels)
            throw new ImageTooLargeException(
                $"Image is {width}x{height} ({pixels:N0} px), exceeding the configured limit of " +
                $"{maxImagePixels:N0} px (PaddleOcrServiceOptions.MaxImagePixels). Raise the limit or downscale " +
                "the image. This guard protects against decompression-bomb / pixel-flood denial of service.");
    }

    /// <summary>
    /// Formats that can never carry alpha decode straight to RGB; everything else goes through the
    /// flattening path, which is exact for opaque images.
    /// </summary>
    internal static bool MayHaveAlpha(ImageFormat? format)
        => format != ImageFormat.Jpeg && format != ImageFormat.Pbm;

    private static ImageFormat? Inspect(ReadOnlySpan<byte> bytes, ImageLoadSettings settings)
    {
        if (settings.MaxImagePixels > 0)
        {
            var info = Image.Identify(bytes);
            GuardPixels(info.Width, info.Height, settings.MaxImagePixels);
            return info.Format;
        }
        return settings.FlattenTransparency ? Image.DetectFormat(bytes) : null;
    }

    private static DecoderOptions CreateDecoderOptions(ImageLoadSettings settings, int maxFrames)
        => settings.MaxImagePixels > 0
            ? new DecoderOptions { MaxFrames = maxFrames, MaxPixels = settings.MaxImagePixels }
            : new DecoderOptions { MaxFrames = maxFrames };

    private static ImageTooLargeException TooLarge(ImageSizeLimitExceededException ex)
        => new(ex.Message + " (PaddleOcrServiceOptions.MaxImagePixels guards against decompression-bomb / pixel-flood denial of service.)");
}
