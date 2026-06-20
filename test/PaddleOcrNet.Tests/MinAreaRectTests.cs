using PaddleOcrNet.Internal.Geometry;
using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="MinAreaRect.Compute"/>. Pins the
/// minimum-area rectangle of a known quad and the corner ordering contract: the returned corners are
/// top-left, top-right, bottom-right, bottom-left (clockwise from the top-left in image coordinates,
/// where y grows downward).
/// </summary>
public class MinAreaRectTests
{
    private static void AssertPoint(OcrPoint expected, OcrPoint actual, double tol = 1e-6)
    {
        Assert.Equal(expected.X, actual.X, tol);
        Assert.Equal(expected.Y, actual.Y, tol);
    }

    [Fact]
    public void Compute_axis_aligned_rectangle_returns_corners_in_TL_TR_BR_BL_order()
    {
        // A 40x20 axis-aligned rectangle given in scrambled order.
        var points = new[]
        {
            new OcrPoint(50, 30), // BR
            new OcrPoint(10, 10), // TL
            new OcrPoint(10, 30), // BL
            new OcrPoint(50, 10), // TR
        };

        var rect = MinAreaRect.Compute(points);

        Assert.Equal(4, rect.Length);
        AssertPoint(new OcrPoint(10, 10), rect[0]); // top-left
        AssertPoint(new OcrPoint(50, 10), rect[1]); // top-right
        AssertPoint(new OcrPoint(50, 30), rect[2]); // bottom-right
        AssertPoint(new OcrPoint(10, 30), rect[3]); // bottom-left
    }

    [Fact]
    public void Compute_recovers_minimum_area_rectangle_of_a_tight_quad()
    {
        // The four corners of a 30x10 axis-aligned box; the min-area rect must be that same box.
        var points = new[]
        {
            new OcrPoint(0, 0),
            new OcrPoint(30, 0),
            new OcrPoint(30, 10),
            new OcrPoint(0, 10),
        };

        var rect = MinAreaRect.Compute(points);

        double area = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = rect[i];
            var b = rect[(i + 1) % 4];
            area += a.X * b.Y - b.X * a.Y;
        }
        area = System.Math.Abs(area) / 2.0;

        Assert.Equal(300.0, area, 3); // 30 * 10
    }

    [Fact]
    public void Compute_first_corner_is_the_top_left_minimum_x_plus_y()
    {
        var points = new[]
        {
            new OcrPoint(5, 5),
            new OcrPoint(25, 5),
            new OcrPoint(25, 15),
            new OcrPoint(5, 15),
        };

        var rect = MinAreaRect.Compute(points);

        // Top-left has the smallest x+y of the four corners.
        double sum0 = rect[0].X + rect[0].Y;
        for (int i = 1; i < 4; i++)
            Assert.True(rect[i].X + rect[i].Y >= sum0 - 1e-6);
    }

    [Fact]
    public void Compute_corner_order_is_clockwise_in_image_coordinates()
    {
        var points = new[]
        {
            new OcrPoint(0, 0),
            new OcrPoint(40, 0),
            new OcrPoint(40, 20),
            new OcrPoint(0, 20),
        };

        var rect = MinAreaRect.Compute(points);

        // Shoelace term sum: Sum((x2 - x1) * (y2 + y1)). In image coordinates (y grows
        // downward) a visually clockwise polygon yields a negative value, matching the
        // canonical top-left -> top-right -> bottom-right -> bottom-left corner order.
        double signed = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = rect[i];
            var b = rect[(i + 1) % 4];
            signed += (b.X - a.X) * (b.Y + a.Y);
        }
        Assert.True(signed < 0, "corners should wind clockwise in image coordinates");
    }
}
