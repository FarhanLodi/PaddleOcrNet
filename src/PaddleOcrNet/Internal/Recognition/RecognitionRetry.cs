using PaddleOcrNet.Internal.Geometry;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Internal.Recognition;

/// <summary>
/// The pure rules behind <see cref="RecognitionOptions.RetryBelowConfidence"/>: how the grown alternate
/// region is built from a line's rectification geometry, and when an alternate reading may replace the
/// original one.
/// </summary>
internal static class RecognitionRetry
{
    /// <summary>
    /// How far the alternate region extends past each end of the line, as a multiple of the line height.
    /// </summary>
    public const double EndGrowth = 0.3;

    /// <summary>
    /// How far the alternate region extends above and below the line, as a multiple of the line height.
    /// </summary>
    public const double SideGrowth = 0.15;

    /// <summary>
    /// The minimum confidence gain an alternate reading must show to replace the original.
    /// </summary>
    public const double MinConfidenceGain = 0.05;

    /// <summary>
    /// The maximum edit distance between an accepted alternate and the original, as a fraction of the
    /// original's length (never less than <see cref="MinEditAllowance"/> edits).
    /// </summary>
    public const double MaxEditFraction = 0.25;

    /// <summary>
    /// The edit distance always allowed regardless of the original's length.
    /// </summary>
    public const int MinEditAllowance = 2;

    /// <summary>
    /// Grows a line's source quad along its own axes: by <see cref="EndGrowth"/> × the line height past each
    /// end and <see cref="SideGrowth"/> × the line height above and below. The line height is the extent
    /// across the text direction — the upright crop's height, or its width for a vertical line that was
    /// rotated for recognition.
    /// </summary>
    /// <param name="geometry">The line crop's rectification geometry.</param>
    /// <returns>The grown quad as top-left, top-right, bottom-right, bottom-left of the upright crop.</returns>
    public static OcrPoint[] GrowQuad(CropGeometry geometry)
    {
        var (ux, acrossLength) = Unit(geometry.TopLeft, geometry.TopRight, fallbackX: 1, fallbackY: 0);
        var (uy, downLength) = Unit(geometry.TopLeft, geometry.BottomLeft, fallbackX: 0, fallbackY: 1);

        // The text runs along the upright crop's x axis, unless the line was vertical (then along y).
        double thickness = geometry.RotatedVertical ? acrossLength : downLength;
        double growX = thickness * (geometry.RotatedVertical ? SideGrowth : EndGrowth);
        double growY = thickness * (geometry.RotatedVertical ? EndGrowth : SideGrowth);

        return new[]
        {
            Offset(geometry.TopLeft, ux, -growX, uy, -growY),
            Offset(geometry.TopRight, ux, growX, uy, -growY),
            Offset(geometry.BottomRight, ux, growX, uy, growY),
            Offset(geometry.BottomLeft, ux, -growX, uy, growY),
        };
    }

    /// <summary>
    /// True when <paramref name="alternate"/> may replace <paramref name="original"/>: it is not blank, its
    /// confidence is at least <see cref="MinConfidenceGain"/> higher, and its text is within
    /// <c>max(2, 25% of the original's length)</c> edits of the original.
    /// </summary>
    /// <param name="original">The line's current reading.</param>
    /// <param name="alternate">The reading of an alternate crop.</param>
    public static bool Accept(RecognizedText original, RecognizedText alternate)
    {
        if (string.IsNullOrWhiteSpace(alternate.Text)) return false;
        // Compare at float precision so a nominal +0.05 is not rejected by float→double rounding.
        if ((float)(alternate.Confidence - original.Confidence) < (float)MinConfidenceGain) return false;

        double allowance = Math.Max(MinEditAllowance, MaxEditFraction * original.Text.Length);
        return Levenshtein(original.Text, alternate.Text) <= allowance;
    }

    /// <summary>
    /// The Levenshtein edit distance (insertions, deletions and substitutions of UTF-16 code units).
    /// </summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static ((double X, double Y) Unit, double Length) Unit(OcrPoint from, OcrPoint to, double fallbackX, double fallbackY)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        return length > 1e-9 ? ((dx / length, dy / length), length) : ((fallbackX, fallbackY), 0);
    }

    private static OcrPoint Offset(OcrPoint p, (double X, double Y) ux, double gx, (double X, double Y) uy, double gy)
        => new(p.X + ux.X * gx + uy.X * gy, p.Y + ux.Y * gx + uy.Y * gy);
}
