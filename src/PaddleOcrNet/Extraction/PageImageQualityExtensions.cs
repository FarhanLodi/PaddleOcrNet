using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Extraction;

/// <summary>Bounded-cost image diagnostics without changing source pixels.</summary>
public static class PageImageQualityExtensions
{
    /// <summary>Samples at most 64 by 64 centers and adjacent pixels. Findings are content-dependent, uncalibrated proxies.</summary>
    public static PageImageQuality AssessImageQuality(this Image<Rgb24> image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        int count = 0, edges = 0;
        double sum = 0, sumSquares = 0, energy = 0;
        var quadrantSums = new double[4];
        var quadrantCounts = new int[4];
        int sx = Math.Max(1, (image.Width + 63) / 64), sy = Math.Max(1, (image.Height + 63) / 64);
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < image.Height; y += sy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = rows.GetRowSpan(y);
                var next = rows.GetRowSpan(Math.Min(y + 1, image.Height - 1));
                for (int x = 0; x < image.Width; x += sx)
                {
                    double luminance = Luminance(row[x]);
                    count++;
                    sum += luminance;
                    sumSquares += luminance * luminance;
                    int quadrant = (y >= image.Height / 2 ? 2 : 0) + (x >= image.Width / 2 ? 1 : 0);
                    quadrantSums[quadrant] += luminance;
                    quadrantCounts[quadrant]++;
                    if (x + 1 < image.Width) { energy += Math.Abs(luminance - Luminance(row[x + 1])); edges++; }
                    if (y + 1 < image.Height) { energy += Math.Abs(luminance - Luminance(next[x])); edges++; }
                }
            }
        });
        var means = Enumerable.Range(0, 4).Where(i => quadrantCounts[i] > 0).Select(i => quadrantSums[i] / quadrantCounts[i]).ToArray();
        return new PageImageQuality
        {
            SampleCount = count,
            LuminanceContrast = Math.Sqrt(Math.Max(0, sumSquares / count - Math.Pow(sum / count, 2))) / 255,
            EdgeEnergy = edges == 0 ? 0 : energy / edges / 255,
            IlluminationVariation = (means.Max() - means.Min()) / 255
        };
    }

    private static double Luminance(Rgb24 pixel) => .2126 * pixel.R + .7152 * pixel.G + .0722 * pixel.B;
}
