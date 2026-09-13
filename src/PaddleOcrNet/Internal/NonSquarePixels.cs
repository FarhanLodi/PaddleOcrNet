using EasyImageSharp;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Internal;

/// <summary>
/// Corrects images whose pixels are not square — typically fax TIFFs scanned at 204×98 or 204×196 DPI.
/// The detector and recognizer assume square pixels, so a 204×98 page reaches them squashed to half
/// height. The image is resampled so both axes share the higher resolution, OCR runs on that copy, and
/// every coordinate is scaled back to the original pixel grid.
/// </summary>
internal static class NonSquarePixels
{
    /// <summary>
    /// Relative difference between the horizontal and vertical resolution (|x/y − 1|) above which the
    /// pixels are treated as non-square. Small differences (rounding, 72 vs 75 DPI) are left alone.
    /// </summary>
    internal const double Tolerance = 0.15;

    /// <summary>
    /// Reads the resolution of an image: its root frame's EXIF/TIFF resolution tags when present (multi-page
    /// TIFFs carry them per page), otherwise the image-level metadata.
    /// </summary>
    internal static (double X, double Y) GetResolution<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (image.Frames.RootFrame.Metadata.ExifProfile is { } exif
            && exif.TryGetValue(ExifTag.XResolution, out var xr)
            && exif.TryGetValue(ExifTag.YResolution, out var yr))
        {
            double x = xr.Value.ToDouble(), y = yr.Value.ToDouble();
            if (x > 0 && y > 0 && double.IsFinite(x) && double.IsFinite(y)) return (x, y);
        }
        return (image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution);
    }

    /// <summary>
    /// Computes the per-axis upscale that makes the pixels square, or returns false when the resolutions
    /// are unknown/invalid or already within <see cref="Tolerance"/> of each other.
    /// </summary>
    internal static bool TryGetScale(double xResolution, double yResolution, out double scaleX, out double scaleY)
    {
        scaleX = scaleY = 1.0;
        if (!(xResolution > 0) || !(yResolution > 0) || !double.IsFinite(xResolution) || !double.IsFinite(yResolution))
            return false;
        if (Math.Abs((xResolution / yResolution) - 1.0) <= Tolerance) return false;

        double target = Math.Max(xResolution, yResolution);
        scaleX = target / xResolution;
        scaleY = target / yResolution;
        return true;
    }

    /// <summary>
    /// Returns a square-pixel copy of <paramref name="image"/> (bilinear), or null when its pixels are
    /// already square. The caller owns the copy.
    /// </summary>
    internal static Image<Rgb24>? CreateSquarePixelCopy(Image<Rgb24> image)
    {
        var (xRes, yRes) = GetResolution(image);
        if (!TryGetScale(xRes, yRes, out double sx, out double sy)) return null;

        int width = Math.Max(1, (int)Math.Round(image.Width * sx));
        int height = Math.Max(1, (int)Math.Round(image.Height * sy));
        if (width == image.Width && height == image.Height) return null;
        return image.Clone(c => c.Resize(width, height, KnownResamplers.Triangle));
    }

    /// <summary>
    /// Scales line coordinates from the resampled grid back to the original one:
    /// <c>x · factorX</c>, <c>y · factorY</c> where factor = original size / resampled size.
    /// </summary>
    internal static IReadOnlyList<OcrLine> MapToSource(IReadOnlyList<OcrLine> lines, double factorX, double factorY)
    {
        if (lines.Count == 0) return lines;
        var mapped = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            var poly = line.BoundingPolygon.Select(p => new OcrPoint(p.X * factorX, p.Y * factorY)).ToArray();
            var b = line.BoundingBox;
            mapped.Add(line with
            {
                BoundingPolygon = poly,
                BoundingBox = new OcrBoundingBox(b.MinX * factorX, b.MinY * factorY, b.MaxX * factorX, b.MaxY * factorY),
            });
        }
        return mapped;
    }

    /// <summary>
    /// Scales an absolute-pixel region into the resampled grid (fractional regions are resolution-independent
    /// and returned unchanged).
    /// </summary>
    internal static OcrRegion ScaleRegion(OcrRegion region, double scaleX, double scaleY)
        => region.Normalized
            ? region
            : region with
            {
                X = region.X * scaleX,
                Y = region.Y * scaleY,
                Width = region.Width * scaleX,
                Height = region.Height * scaleY,
            };
}
