using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace PaddleOcrNet.Internal;

/// <summary>
/// Composites an image with an alpha channel onto an opaque background before OCR. A plain RGB
/// conversion simply drops alpha, so black text on a fully transparent (0,0,0,0) background — the common
/// export from design tools — becomes an all-black image. The background is white, unless the visible
/// content is itself light (mean alpha-weighted luminance above <see cref="LightContentLuminance"/>, i.e.
/// white-on-transparent), in which case it is black so the text keeps its contrast.
/// </summary>
internal static class TransparencyFlattener
{
    /// <summary>
    /// Mean luminance (0–255) of the visible pixels above which the content is treated as light and
    /// flattened onto black instead of white.
    /// </summary>
    internal const double LightContentLuminance = 160.0;

    /// <summary>
    /// Returns an opaque <see cref="Rgb24"/> copy of <paramref name="source"/> (all frames, metadata kept).
    /// A fully opaque image is converted exactly as a direct RGB decode would be. The source is not disposed.
    /// </summary>
    internal static Image<Rgb24> Flatten(Image<Rgba32> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = source.CloneAs<Rgb24>();
        try
        {
            for (int f = 0; f < source.Frames.Count; f++)
            {
                FlattenFrame(source.Frames[f], result.Frames[f]);
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Chooses the background for the given visible-content statistics: black (0) for light content,
    /// white (255) otherwise, including when nothing is visible at all.
    /// </summary>
    internal static byte ChooseBackground(double alphaWeightedLuminance, double totalAlpha)
        => totalAlpha > 0 && alphaWeightedLuminance / totalAlpha > LightContentLuminance ? (byte)0 : (byte)255;

    private static void FlattenFrame(ImageFrame<Rgba32> source, ImageFrame<Rgb24> destination)
    {
        int width = source.Width, height = source.Height;

        // Pass 1: is anything translucent, and how bright is what is visible?
        bool translucent = false;
        double lumSum = 0, alphaSum = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgba32> row = source.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            {
                byte a = row[x].A;
                if (a == 0) { translucent = true; continue; }
                if (a != 255) translucent = true;
                lumSum += ((0.299 * row[x].R) + (0.587 * row[x].G) + (0.114 * row[x].B)) * a;
                alphaSum += a;
            }
        }

        // Fully opaque: the CloneAs conversion is already exact.
        if (!translucent) return;

        int bg = ChooseBackground(lumSum, alphaSum);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgba32> src = source.GetRowSpan(y);
            Span<Rgb24> dst = destination.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                int a = src[x].A;
                if (a == 255) continue; // already converted
                int inv = 255 - a;
                dst[x] = new Rgb24(
                    (byte)(((src[x].R * a) + (bg * inv) + 127) / 255),
                    (byte)(((src[x].G * a) + (bg * inv) + 127) / 255),
                    (byte)(((src[x].B * a) + (bg * inv) + 127) / 255));
            }
        }
    }
}
