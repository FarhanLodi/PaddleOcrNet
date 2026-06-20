using PaddleOcrNet.Models;
using PaddleOcrNet.Structure.ReadingOrder;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="XyCutOrderer.Order"/> — the recursive
/// XY-cut reading-order pass. Reading order is verified on three layouts: a classic two-column page (whole
/// left column read top-to-bottom before the whole right column), a single-column page (already in order),
/// and a nested case where one column is itself split into two sub-columns.
/// <para>
/// XY-cut is an algorithm that lives behind <see cref="XyCutOrderer.Order"/>; to make the expected reading
/// order independent of where the body ultimately lands, the correct permutation is pinned against a local
/// reference XY-cut (mirroring the reference-helper convention of the sorted-boxes / unclip tests). The real
/// <see cref="XyCutOrderer.Order"/> is then asserted to agree with that reference.
/// </para>
/// </summary>
public class XyCutOrdererTests
{
    private static OcrBoundingBox Box(double minX, double minY, double maxX, double maxY)
        => new(minX, minY, maxX, maxY);

    // -----------------------------------------------------------------------------------------------------
    // Local reference XY-cut: repeatedly split the active index set by the widest clear gap — first try a
    // horizontal cut (a clear band of empty Y between a top group and a bottom group), else a vertical cut
    // (a clear gutter of empty X between a left group and a right group) — recursing until each leaf holds a
    // single block, then concatenate leaves in natural top-to-bottom / left-to-right order. Returns a
    // permutation of input indices in reading order. This is the behaviour the real orderer must produce.
    // -----------------------------------------------------------------------------------------------------
    private static IReadOnlyList<int> XyCutReference(IReadOnlyList<OcrBoundingBox> blocks)
    {
        var order = new List<int>(blocks.Count);
        Cut(Enumerable.Range(0, blocks.Count).ToList());
        return order;

        void Cut(List<int> idx)
        {
            if (idx.Count <= 1)
            {
                order.AddRange(idx);
                return;
            }

            // Evaluate the widest clear gap on each axis: a horizontal cut splits the set into a top band
            // and a bottom band (read top→bottom); a vertical cut splits it into a left column and a right
            // column (read left→right). Take whichever axis offers the wider clear gap; on a tie prefer the
            // vertical (column) cut so a true multi-column page reads each column fully before the next.
            double hGap = BestGap(idx, horizontal: true, out var top, out var bottom);
            double vGap = BestGap(idx, horizontal: false, out var left, out var right);

            if (vGap > 0 && vGap >= hGap)
            {
                Cut(left);
                Cut(right);
                return;
            }
            if (hGap > 0)
            {
                Cut(top);
                Cut(bottom);
                return;
            }

            // No clear gap on either axis: fall back to natural reading order of the remaining blocks.
            foreach (var i in idx.OrderBy(i => blocks[i].MinY).ThenBy(i => blocks[i].MinX))
                order.Add(i);
        }

        // Returns the width of the widest clear gap (band/gutter that no block straddles) on the requested
        // axis and, via out-params, the two index groups either side of it. Returns 0 when there is no gap.
        double BestGap(List<int> idx, bool horizontal, out List<int> first, out List<int> second)
        {
            first = new List<int>();
            second = new List<int>();

            // Sort intervals along the cut axis and find the largest gap between consecutive blocks where no
            // earlier block extends across the gap (a clear band/gutter).
            var sorted = idx
                .OrderBy(i => horizontal ? blocks[i].MinY : blocks[i].MinX)
                .ToList();

            double bestGap = 0;
            int bestAt = -1;
            double runningMax = horizontal ? blocks[sorted[0]].MaxY : blocks[sorted[0]].MaxX;

            for (int k = 1; k < sorted.Count; k++)
            {
                double start = horizontal ? blocks[sorted[k]].MinY : blocks[sorted[k]].MinX;
                double gap = start - runningMax;
                if (gap > bestGap)
                {
                    bestGap = gap;
                    bestAt = k;
                }
                double end = horizontal ? blocks[sorted[k]].MaxY : blocks[sorted[k]].MaxX;
                if (end > runningMax) runningMax = end;
            }

            if (bestAt < 0 || bestGap <= 0)
                return 0;

            first = sorted.Take(bestAt).ToList();
            second = sorted.Skip(bestAt).ToList();
            return bestGap;
        }
    }

    [Fact]
    public void XyCutReference_two_column_reads_whole_left_column_then_whole_right_column()
    {
        // Two columns (left x in [0,90], right x in [110,200]) with a clear 20px gutter between them; two
        // rows of text in each column. Given scrambled, reading order must be L-top, L-bottom, R-top, R-bottom.
        var blocks = new[]
        {
            /* 0 */ Box(110, 0, 200, 40),   // right-top
            /* 1 */ Box(0, 60, 90, 100),    // left-bottom
            /* 2 */ Box(0, 0, 90, 40),      // left-top
            /* 3 */ Box(110, 60, 200, 100), // right-bottom
        };

        var order = XyCutReference(blocks);

        Assert.Equal(new[] { 2, 1, 0, 3 }, order.ToArray());
    }

    [Fact]
    public void XyCutReference_single_column_preserves_top_to_bottom_order()
    {
        // One column, three stacked rows given out of order. Reading order = strictly top to bottom.
        var blocks = new[]
        {
            /* 0 */ Box(0, 200, 100, 240), // bottom
            /* 1 */ Box(0, 0, 100, 40),    // top
            /* 2 */ Box(0, 100, 100, 140), // middle
        };

        var order = XyCutReference(blocks);

        Assert.Equal(new[] { 1, 2, 0 }, order.ToArray());
    }

    [Fact]
    public void XyCutReference_nested_column_split_inside_a_band_reads_recursively()
    {
        // A full-width header band on top, then a two-column body below it. Expected reading order:
        // header, then left-body (top, bottom), then right-body. Exercises a horizontal cut whose lower
        // band is itself split vertically (the nested case).
        var blocks = new[]
        {
            /* 0 */ Box(110, 60, 200, 100), // body right
            /* 1 */ Box(0, 0, 200, 30),     // full-width header
            /* 2 */ Box(0, 100, 90, 140),   // body left-bottom
            /* 3 */ Box(0, 60, 90, 100),    // body left-top
        };

        var order = XyCutReference(blocks);

        Assert.Equal(new[] { 1, 3, 2, 0 }, order.ToArray());
    }

    // ---- The real orderer must agree with the reference on every layout above. -------------------------

    [Fact]
    public void Order_two_column_matches_reference_left_column_then_right()
    {
        var blocks = new[]
        {
            Box(110, 0, 200, 40),
            Box(0, 60, 90, 100),
            Box(0, 0, 90, 40),
            Box(110, 60, 200, 100),
        };

        var actual = XyCutOrderer.Order(blocks);

        Assert.Equal(XyCutReference(blocks).ToArray(), actual.ToArray());
    }

    [Fact]
    public void Order_single_column_matches_reference_top_to_bottom()
    {
        var blocks = new[]
        {
            Box(0, 200, 100, 240),
            Box(0, 0, 100, 40),
            Box(0, 100, 100, 140),
        };

        var actual = XyCutOrderer.Order(blocks);

        Assert.Equal(XyCutReference(blocks).ToArray(), actual.ToArray());
    }

    [Fact]
    public void Order_nested_layout_matches_reference()
    {
        var blocks = new[]
        {
            Box(110, 60, 200, 100),
            Box(0, 0, 200, 30),
            Box(0, 100, 90, 140),
            Box(0, 60, 90, 100),
        };

        var actual = XyCutOrderer.Order(blocks);

        Assert.Equal(XyCutReference(blocks).ToArray(), actual.ToArray());
    }

    [Fact]
    public void Order_returns_a_permutation_of_every_input_index()
    {
        var blocks = new[]
        {
            Box(110, 0, 200, 40),
            Box(0, 60, 90, 100),
            Box(0, 0, 90, 40),
            Box(110, 60, 200, 100),
        };

        var order = XyCutOrderer.Order(blocks);

        Assert.Equal(blocks.Length, order.Count);
        Assert.Equal(Enumerable.Range(0, blocks.Length), order.OrderBy(i => i));
    }
}
