using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for reading-order box sorting.
/// <para>
/// Covers two things on a small box set: (1) the real <see cref="PaddleOcrService.SortLinesByReadingOrder"/>
/// helper that the engine uses to order recognized lines; and (2) the upstream PaddleOCR <c>sorted_boxes</c>
/// rule — sort by top-edge Y, then for boxes within a 10px Y tolerance swap so the left one comes first,
/// otherwise break out of the inner pass (the "else: break" in PaddleOCR's <c>sorted_boxes</c>). The
/// classic algorithm is verified against a local reference so it is pinned regardless of where the detector
/// pipeline ultimately implements it.
/// </para>
/// </summary>
public class SortedBoxesReadingOrderTests
{
    private static OcrLine Line(string text, double x, double y, double w, double h)
    {
        var box = new OcrBoundingBox(x, y, x + w, y + h);
        var poly = new[]
        {
            new OcrPoint(x, y), new OcrPoint(x + w, y),
            new OcrPoint(x + w, y + h), new OcrPoint(x, y + h),
        };
        return new OcrLine { Text = text, Confidence = 1.0, BoundingBox = box, BoundingPolygon = poly };
    }

    [Fact]
    public void SortLinesByReadingOrder_orders_top_to_bottom_then_left_to_right()
    {
        var lines = new[]
        {
            Line("row2", 0, 100, 50, 20),
            Line("row1right", 60, 0, 50, 20),
            Line("row1left", 0, 0, 50, 20),
        };

        var ordered = PaddleOcrService.SortLinesByReadingOrder(lines);

        Assert.Equal(new[] { "row1left", "row1right", "row2" }, ordered.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void SortLinesByReadingOrder_single_line_returned_unchanged()
    {
        var lines = new[] { Line("only", 5, 5, 10, 10) };
        Assert.Single(PaddleOcrService.SortLinesByReadingOrder(lines));
    }

    [Fact]
    public void SortLinesByReadingOrder_jittered_same_row_kept_left_to_right()
    {
        // Two boxes on the same visual row with a small (5px) vertical jitter, given right-then-left.
        // They must come out left-to-right rather than being split into separate rows.
        var lines = new[]
        {
            Line("World", 200, 5, 80, 30),
            Line("Hello", 0, 0, 80, 30),
        };

        var ordered = PaddleOcrService.SortLinesByReadingOrder(lines);

        Assert.Equal(new[] { "Hello", "World" }, ordered.Select(l => l.Text).ToArray());
    }

    // --- Classic PaddleOCR sorted_boxes: 10px Y tolerance + else-break ------------------------------------

    /// <summary>
    /// Local reference of PaddleOCR's <c>sorted_boxes</c> (utility/operators). Boxes are sorted by
    /// (topY, leftX); then a single adjacent-swap pass enforces left-first ordering for any pair whose
    /// top edges differ by less than 10px, breaking out of the inner comparison the moment a pair exceeds
    /// the tolerance (the upstream <c>else: break</c>). Each box is its top-left (x, y).
    /// </summary>
    private static (double X, double Y)[] SortedBoxesReference((double X, double Y)[] boxes)
    {
        var arr = boxes
            .OrderBy(b => b.Y)
            .ThenBy(b => b.X)
            .ToArray();

        for (int i = 0; i < arr.Length - 1; i++)
        {
            for (int j = i; j >= 0; j--)
            {
                if (System.Math.Abs(arr[j + 1].Y - arr[j].Y) < 10 && arr[j + 1].X < arr[j].X)
                {
                    (arr[j], arr[j + 1]) = (arr[j + 1], arr[j]);
                }
                else
                {
                    break; // PaddleOCR's `else: break` — stop once the Y gap exceeds the tolerance.
                }
            }
        }
        return arr;
    }

    [Fact]
    public void SortedBoxes_within_10px_tolerance_orders_left_to_right()
    {
        // Same row (Y differs by 8 < 10): the left box must come first even though it is listed second.
        var boxes = new[] { (X: 100.0, Y: 8.0), (X: 10.0, Y: 0.0) };

        var sorted = SortedBoxesReference(boxes);

        Assert.Equal((10.0, 0.0), sorted[0]);
        Assert.Equal((100.0, 8.0), sorted[1]);
    }

    [Fact]
    public void SortedBoxes_beyond_10px_tolerance_keeps_top_to_bottom_order()
    {
        // Y differs by 40 (>= 10): the higher box stays first regardless of X (else: break path).
        var boxes = new[] { (X: 10.0, Y: 0.0), (X: 100.0, Y: 40.0) };

        var sorted = SortedBoxesReference(boxes);

        Assert.Equal((10.0, 0.0), sorted[0]);   // top row first
        Assert.Equal((100.0, 40.0), sorted[1]); // bottom row second
    }

    [Fact]
    public void SortedBoxes_full_two_row_grid_reads_in_human_order()
    {
        // Two rows, each with two columns, plus intra-row Y jitter < 10px. Expect: TL, TR, BL, BR.
        var boxes = new[]
        {
            (X: 200.0, Y: 4.0),    // top-right
            (X: 10.0, Y: 0.0),     // top-left
            (X: 200.0, Y: 100.0),  // bottom-right
            (X: 10.0, Y: 105.0),   // bottom-left (jittered below its row-mate)
        };

        var sorted = SortedBoxesReference(boxes);

        Assert.Equal(new[]
        {
            (10.0, 0.0),    // TL
            (200.0, 4.0),   // TR
            (10.0, 105.0),  // BL
            (200.0, 100.0), // BR
        }, sorted);
    }
}
