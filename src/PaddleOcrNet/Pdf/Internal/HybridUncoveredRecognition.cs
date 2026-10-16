using System.Diagnostics;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;

namespace PaddleOcrNet.Pdf.Internal;

/// <summary>Opt-in source-frame scheduling; native coverage is a cost heuristic, never visual verification.</summary>
internal static class HybridUncoveredRecognition
{
    internal static void Validate(RecognitionOptions? options)
    {
        if (options is null || options.UseDocOrientation || options.UseDocUnwarp
            || options.Preprocessing.Deskew || options.Preprocessing.Denoise || options.Preprocessing.Binarize
            || options.Preprocessing.DetectOrientation || options.Preprocessing.CorrectNonSquarePixels)
            throw new ArgumentException("HybridRecognizeUncoveredOnly requires explicit RecognitionOptions with UseDocOrientation=false, UseDocUnwarp=false, and all Preprocessing flags disabled (including CorrectNonSquarePixels). Use full-page Hybrid recognition to apply page preprocessing.", nameof(options));
    }

    internal static async Task<OcrResult> RecognizeAsync(IPaddleOcrService service, Image<Rgb24> image,
        IReadOnlyList<OcrLine> native, IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions options, CancellationToken cancellationToken)
    {
        Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var validNative = native.Where(l => HybridTextLayer.IsReliable(l, image.Width, image.Height)
            && WithinRegion(l.BoundingBox, options.Region, image.Width, image.Height)).ToArray();
        try
        {
            var detected = await service.DetectRegionsAsync(image, options, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var uncovered = new List<IReadOnlyList<OcrPoint>>();
            foreach (var region in detected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var polygon = region.BoundingPolygon;
                if (polygon.Count < 3 || polygon.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)
                    || p.X < 0 || p.Y < 0 || p.X > image.Width || p.Y > image.Height)) continue;
                var box = new OcrBoundingBox(polygon.Min(p => p.X), polygon.Min(p => p.Y), polygon.Max(p => p.X), polygon.Max(p => p.Y));
                if (box.Width <= 0 || box.Height <= 0 || !WithinRegion(box, options.Region, image.Width, image.Height)) continue;
                if (!Covered(box, validNative)) uncovered.Add(polygon);
            }
            if (uncovered.Count == 0)
                return OcrResult.Empty with
                {
                    Languages = languages.ToCodes(), SourceWidth = image.Width, SourceHeight = image.Height,
                    Duration = stopwatch.Elapsed, Coordinates = new OcrCoordinateProvenance { RegionApplied = options.Region is not null }
                };
            var recognized = await service.RecognizeRegionsAsync(image, uncovered, languages, options, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return recognized with
            {
                Duration = stopwatch.Elapsed,
                // RecognizeRegionsAsync has no page ROI stage of its own. The detector's ROI has
                // already scoped these polygons, so carry that applied stage into result provenance.
                Coordinates = recognized.Coordinates is { } coordinates
                    ? coordinates with { RegionApplied = options.Region is not null }
                    : null
            };
        }
        catch (NotSupportedException ex)
        {
            throw new NotSupportedException("HybridRecognizeUncoveredOnly requires a service implementing DetectRegionsAsync and RecognizeRegionsAsync. Disable this option for full-page Hybrid recognition.", ex);
        }
    }

    internal static bool Covered(OcrBoundingBox region, IReadOnlyList<OcrLine> native)
    {
        // Union clipped rectangles so duplicate native layers never inflate coverage. Native boxes
        // much larger than the detected region cannot suppress recognition through containment alone.
        var intersections = new List<OcrBoundingBox>();
        foreach (var line in native)
        {
            var box = line.BoundingBox;
            double x0 = Math.Max(region.MinX, box.MinX), y0 = Math.Max(region.MinY, box.MinY);
            double x1 = Math.Min(region.MaxX, box.MaxX), y1 = Math.Min(region.MaxY, box.MaxY);
            double area = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0);
            if (box.Width > 0 && box.Height > 0 && area / (box.Width * box.Height) >= .5)
                intersections.Add(new OcrBoundingBox(x0, y0, x1, y1));
        }
        var xs = intersections.SelectMany(b => new[] { b.MinX, b.MaxX }).Distinct().Order().ToArray();
        double union = 0;
        for (int i = 1; i < xs.Length; i++)
        {
            double covered = 0, end = double.NegativeInfinity;
            foreach (var box in intersections.Where(b => b.MinX < xs[i] && b.MaxX > xs[i - 1]).OrderBy(b => b.MinY))
            {
                covered += Math.Max(0, box.MaxY - Math.Max(end, box.MinY));
                end = Math.Max(end, box.MaxY);
            }
            union += (xs[i] - xs[i - 1]) * covered;
        }
        return region.Width > 0 && region.Height > 0 && union / (region.Width * region.Height) >= .95;
    }

    private static bool WithinRegion(OcrBoundingBox box, OcrRegion? region, int width, int height)
    {
        if (region is null) return true;
        var bounds = region.Value.Resolve(width, height);
        return box.MinX >= bounds.X && box.MinY >= bounds.Y
            && box.MaxX <= bounds.X + bounds.Width && box.MaxY <= bounds.Y + bounds.Height;
    }
}
