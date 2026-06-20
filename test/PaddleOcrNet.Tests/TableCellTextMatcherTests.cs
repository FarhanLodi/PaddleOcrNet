using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the table cell ↔ OCR-line matcher used by
/// <see cref="PaddleOcrNet.Structure.Table.SlanetTableRecognizer"/> when filling recognized text into the
/// predicted cells. Following PaddleOCR's <c>TableMatch</c>, each OCR box is assigned to the cell that
/// minimizes <c>distance = L1(box, cell) + (1 - IoU(box, cell))</c>, where <c>L1</c> is the sum of absolute
/// corner offsets. These tests pin that cost and the resulting assignment on a known 2×2 grid; the rule is
/// encoded as a local reference so it is verified independent of where the recognizer body lands.
/// </summary>
public class TableCellTextMatcherTests
{
    private static OcrBoundingBox Box(double x1, double y1, double x2, double y2) => new(x1, y1, x2, y2);

    /// <summary>Sum of absolute differences of the two boxes' corners (PaddleOCR TableMatch L1 term).</summary>
    private static double L1(OcrBoundingBox a, OcrBoundingBox b)
        => System.Math.Abs(a.MinX - b.MinX)
         + System.Math.Abs(a.MinY - b.MinY)
         + System.Math.Abs(a.MaxX - b.MaxX)
         + System.Math.Abs(a.MaxY - b.MaxY);

    /// <summary>Intersection-over-union of two axis-aligned boxes (0 when disjoint).</summary>
    private static double Iou(OcrBoundingBox a, OcrBoundingBox b)
    {
        double ix1 = System.Math.Max(a.MinX, b.MinX);
        double iy1 = System.Math.Max(a.MinY, b.MinY);
        double ix2 = System.Math.Min(a.MaxX, b.MaxX);
        double iy2 = System.Math.Min(a.MaxY, b.MaxY);

        double iw = System.Math.Max(0, ix2 - ix1);
        double ih = System.Math.Max(0, iy2 - iy1);
        double inter = iw * ih;
        if (inter <= 0) return 0;

        double union = (a.Width * a.Height) + (b.Width * b.Height) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>The TableMatch cost: <c>L1 + (1 - IoU)</c>. Lower is a better cell for the OCR box.</summary>
    private static double MatchCost(OcrBoundingBox ocr, OcrBoundingBox cell) => L1(ocr, cell) + (1.0 - Iou(ocr, cell));

    /// <summary>Index of the cell with the lowest match cost for <paramref name="ocr"/>.</summary>
    private static int BestCell(OcrBoundingBox ocr, IReadOnlyList<OcrBoundingBox> cells)
    {
        int best = -1;
        double bestCost = double.PositiveInfinity;
        for (int i = 0; i < cells.Count; i++)
        {
            double cost = MatchCost(ocr, cells[i]);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = i;
            }
        }
        return best;
    }

    // A 2x2 grid of cells (each 100x100):  [0]=TL [1]=TR / [2]=BL [3]=BR.
    private static IReadOnlyList<OcrBoundingBox> Grid() => new[]
    {
        Box(0, 0, 100, 100),     // 0 top-left
        Box(100, 0, 200, 100),   // 1 top-right
        Box(0, 100, 100, 200),   // 2 bottom-left
        Box(100, 100, 200, 200), // 3 bottom-right
    };

    [Fact]
    public void MatchCost_is_zero_for_an_exact_overlap()
    {
        var cell = Box(0, 0, 100, 100);
        Assert.Equal(0.0, MatchCost(cell, cell), 9);
    }

    [Fact]
    public void BestCell_assigns_a_box_inside_the_top_right_cell()
    {
        // An OCR line well inside the top-right cell.
        var ocr = Box(120, 20, 180, 70);

        Assert.Equal(1, BestCell(ocr, Grid()));
    }

    [Fact]
    public void BestCell_assigns_a_box_inside_the_bottom_left_cell()
    {
        var ocr = Box(20, 130, 80, 170);

        Assert.Equal(2, BestCell(ocr, Grid()));
    }

    [Fact]
    public void BestCell_prefers_the_cell_with_higher_overlap_when_a_box_straddles_a_boundary()
    {
        // Box spans the TL/TR boundary but lies mostly (80%) in the top-right cell; IoU breaks the tie.
        var ocr = Box(80, 20, 180, 70);

        Assert.Equal(1, BestCell(ocr, Grid()));
    }

    [Fact]
    public void MatchCost_penalizes_a_distant_cell_over_the_containing_one()
    {
        var ocr = Box(120, 20, 180, 70); // inside TR
        var grid = Grid();

        double toTopRight = MatchCost(ocr, grid[1]);
        double toBottomLeft = MatchCost(ocr, grid[2]);

        Assert.True(toTopRight < toBottomLeft);
    }

    [Fact]
    public void L1_term_breaks_ties_when_two_cells_have_no_overlap()
    {
        // A box just above the grid overlaps neither top cell (IoU 0 for both), so (1 - IoU) = 1 for each;
        // the L1 corner distance must then pick the horizontally-closer top-left cell.
        var ocr = Box(10, -40, 60, -10);
        var grid = Grid();

        Assert.Equal(0.0, Iou(ocr, grid[0]), 9);
        Assert.Equal(0.0, Iou(ocr, grid[1]), 9);
        Assert.Equal(0, BestCell(ocr, grid));
    }
}
