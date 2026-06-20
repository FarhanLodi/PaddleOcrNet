using PaddleOcrNet.Internal.Detection;
using PaddleOcrNet.Internal.Geometry;
using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the previously-inert <see cref="DetectionOptions"/>
/// knobs now honored by DB post-processing: <see cref="DetectionOptions.UseDilation"/> (2x2 morphological
/// dilation of the binary map, <see cref="Morphology.Dilate2x2"/>) and <see cref="DetectionOptions.ScoreMode"/>
/// (<c>box_score_fast</c> bounding-box crop vs <c>box_score_slow</c> exact region mask). These exercise the
/// real code paths in <see cref="DBPostProcess.GetBoxes"/> on hand-built probability maps so the wiring
/// can't silently regress, without needing the detection model.
/// </summary>
public class DetectionOptionsBehaviorTests
{
    // ---- Morphology.Dilate2x2 ---------------------------------------------------------------------

    [Fact]
    public void Dilate2x2_grows_a_single_pixel_into_a_2x2_block_up_and_left()
    {
        // OpenCV's np.ones((2,2)) kernel is anchored at (1,1), so a lone "on" pixel at (x,y) lights up
        // the output at (x,y), (x+1,y), (x,y+1), (x+1,y+1) — i.e. it grows down and right.
        const int w = 5, h = 5;
        var mask = new byte[w * h];
        mask[2 * w + 2] = 1; // single on-pixel at (2,2)

        var dilated = Morphology.Dilate2x2(mask, w, h);

        var on = new System.Collections.Generic.HashSet<(int X, int Y)>();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (dilated[y * w + x] != 0)
                {
                    on.Add((x, y));
                }
            }
        }

        Assert.Equal(
            new System.Collections.Generic.HashSet<(int, int)> { (2, 2), (3, 2), (2, 3), (3, 3) },
            on);
    }

    [Fact]
    public void Dilate2x2_bridges_a_one_pixel_horizontal_gap()
    {
        // Two on-pixels with a single off-pixel between them: dilation fills the gap so the two regions
        // touch (the broken-stroke case use_dilation targets).
        const int w = 6, h = 3;
        var mask = new byte[w * h];
        mask[1 * w + 1] = 1; // (1,1)
        mask[1 * w + 3] = 1; // (3,1) — gap at (2,1)

        var dilated = Morphology.Dilate2x2(mask, w, h);

        // The gap pixel (2,1) is now on because its 2x2 anchor covers the on-pixel at (1,1).
        Assert.NotEqual(0, dilated[1 * w + 2]);
    }

    [Fact]
    public void Dilate2x2_leaves_an_all_zero_mask_empty()
    {
        const int w = 4, h = 4;
        var dilated = Morphology.Dilate2x2(new byte[w * h], w, h);
        Assert.All(dilated, b => Assert.Equal(0, b));
    }

    // ---- ScoreMode Fast vs Slow -------------------------------------------------------------------

    [Fact]
    public void ScoreSlow_exceeds_ScoreFast_when_region_is_non_rectangular()
    {
        // Build a probability map with a solid high-probability rectangle PLUS a thin diagonal "tail" of
        // high probability, so the connected region is non-rectangular and its axis-aligned bounding box
        // contains a corner of zero-probability background.
        //
        //   The bounding-box crop (fast) averages in those zero corners and is dragged DOWN;
        //   the exact region mask (slow) averages only the lit pixels and stays HIGH.
        const int w = 32, h = 32;
        var prob = new float[w * h];

        void Lit(int x, int y) => prob[y * w + x] = 0.95f;

        // Solid 12x8 block at the top-left of the text area.
        for (int y = 6; y < 14; y++)
        {
            for (int x = 6; x < 18; x++)
            {
                Lit(x, y);
            }
        }
        // Diagonal tail extending toward the bottom-right, enlarging the bounding box with a region
        // whose enclosing rectangle is mostly background.
        for (int k = 0; k < 8; k++)
        {
            Lit(17 + k, 13 + k);
            Lit(18 + k, 13 + k);
        }

        float slow = ScoreFor(prob, w, h, DetectionScoreMode.Slow);
        float fast = ScoreFor(prob, w, h, DetectionScoreMode.Fast);

        Assert.True(slow > 0f && fast > 0f, "both modes must score the region");
        Assert.True(slow > fast, $"slow ({slow}) should exceed fast ({fast}) for a non-rectangular region");
        // Slow averages only the ~0.95 lit pixels, so it sits near the lit value.
        Assert.True(slow > 0.9f, $"slow score {slow} should be close to the lit probability (0.95)");
    }

    [Fact]
    public void ScoreMode_selects_box_only_when_slow_clears_the_threshold()
    {
        // With a box-score floor set between the fast and slow scores, only Slow mode should keep the box.
        const int w = 32, h = 32;
        var prob = new float[w * h];
        void Lit(int x, int y) => prob[y * w + x] = 0.95f;

        for (int y = 6; y < 14; y++)
        {
            for (int x = 6; x < 18; x++)
            {
                Lit(x, y);
            }
        }
        for (int k = 0; k < 8; k++)
        {
            Lit(17 + k, 13 + k);
            Lit(18 + k, 13 + k);
        }

        float slow = ScoreFor(prob, w, h, DetectionScoreMode.Slow);
        float fast = ScoreFor(prob, w, h, DetectionScoreMode.Fast);
        double floor = (slow + fast) / 2.0; // between the two scores

        var slowBoxes = DBPostProcess.GetBoxes(prob, w, h, OptionsWith(DetectionScoreMode.Slow, floor));
        var fastBoxes = DBPostProcess.GetBoxes(prob, w, h, OptionsWith(DetectionScoreMode.Fast, floor));

        Assert.NotEmpty(slowBoxes); // slow score clears the floor
        Assert.Empty(fastBoxes);    // fast score is below the floor and the box is dropped
    }

    [Fact]
    public void Fast_and_slow_agree_on_a_solid_rectangle()
    {
        // For a perfectly rectangular region the bounding-box crop equals the region mask, so both score
        // modes produce the same value — the slow path is byte-identical to fast in the rectangular case.
        const int w = 32, h = 32;
        var prob = new float[w * h];
        for (int y = 8; y < 20; y++)
        {
            for (int x = 8; x < 24; x++)
            {
                prob[y * w + x] = 0.9f;
            }
        }

        float slow = ScoreFor(prob, w, h, DetectionScoreMode.Slow);
        float fast = ScoreFor(prob, w, h, DetectionScoreMode.Fast);

        Assert.Equal(fast, slow, 3);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary>
    /// Runs <see cref="DBPostProcess.GetBoxes"/> with a near-zero box-score floor (so the single region
    /// always survives) and returns that region's score under <paramref name="mode"/>.
    /// </summary>
    private static float ScoreFor(float[] prob, int w, int h, DetectionScoreMode mode)
    {
        var boxes = DBPostProcess.GetBoxes(prob, w, h, OptionsWith(mode, 0.01));
        Assert.NotEmpty(boxes);
        return boxes[0].Score;
    }

    private static DetectionOptions OptionsWith(DetectionScoreMode mode, double boxThreshold) => new()
    {
        ScoreMode = mode,
        BoxThreshold = boxThreshold,
        DetThreshold = 0.3,
        UnclipRatio = 1.5,
        MinSize = 3,
    };
}
