using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the DB-detection "unclip" offset distance.
/// PaddleOCR expands each shrunken DB contour outward by a Clipper/Vatti offset of
/// <c>distance = polygonArea * unclipRatio / polygonPerimeter</c> (see PaddleOCR
/// <c>db_postprocess.unclip</c>). These tests pin that formula on known rectangles so the geometry the
/// detector relies on can't silently drift; they evaluate the formula directly (no model, no Clipper run).
/// </summary>
public class DbUnclipFormulaTests
{
    /// <summary>
    /// The DB unclip offset distance for a polygon: <c>area * ratio / perimeter</c>. Mirrors the value the
    /// detector feeds to Clipper2's <c>InflatePaths</c> when expanding a contour back to glyph extent.
    /// </summary>
    private static double UnclipDistance(IReadOnlyList<OcrPoint> polygon, double ratio)
    {
        double area = 0, perimeter = 0;
        int n = polygon.Count;
        for (int i = 0; i < n; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % n];
            area += a.X * b.Y - b.X * a.Y;
            perimeter += System.Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        }
        area = System.Math.Abs(area) / 2.0;
        return area * ratio / perimeter;
    }

    [Fact]
    public void UnclipDistance_on_a_known_rectangle_matches_area_times_ratio_over_perimeter()
    {
        // 40 x 20 rectangle: area = 800, perimeter = 120. With ratio 1.5 -> 800 * 1.5 / 120 = 10.
        var rect = new[]
        {
            new OcrPoint(0, 0), new OcrPoint(40, 0),
            new OcrPoint(40, 20), new OcrPoint(0, 20),
        };

        double d = UnclipDistance(rect, 1.5);

        Assert.Equal(10.0, d, 6);
    }

    [Fact]
    public void UnclipDistance_scales_linearly_with_the_ratio()
    {
        var rect = new[]
        {
            new OcrPoint(0, 0), new OcrPoint(40, 0),
            new OcrPoint(40, 20), new OcrPoint(0, 20),
        };

        Assert.Equal(2.0 * UnclipDistance(rect, 1.0), UnclipDistance(rect, 2.0), 6);
    }

    [Fact]
    public void UnclipDistance_on_a_unit_square_equals_quarter_of_ratio()
    {
        // 1 x 1 square: area = 1, perimeter = 4, so distance = ratio / 4.
        var square = new[]
        {
            new OcrPoint(0, 0), new OcrPoint(1, 0),
            new OcrPoint(1, 1), new OcrPoint(0, 1),
        };

        Assert.Equal(1.5 / 4.0, UnclipDistance(square, 1.5), 6);
    }
}
