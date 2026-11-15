using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Internal.Geometry;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Coordinate-mapback unit tests and a gated real-model matrix on a synthetic non-square page.
/// </summary>
public class DocOrientationMapbackTests
{
    // ---- OrientationMapper: forward point mapping -----------------------------------------------------

    [Fact]
    public void RotatePoint_0_degrees_is_identity()
    {
        var p = new OcrPoint(12.5, 34.5);
        Assert.Equal(p, OrientationMapper.RotatePoint(p, 0, 100, 50));
    }

    [Fact]
    public void RotatePoint_90_cw_maps_top_left_to_top_right()
    {
        // 100×50 image rotated 90° CW → 50×100 frame. Continuous corner (0,0) → (H, 0) = (50, 0).
        var p = OrientationMapper.RotatePoint(new OcrPoint(0, 0), 90, 100, 50);
        Assert.Equal(new OcrPoint(50, 0), p);

        // Interior point: (x,y) → (H − y, x).
        var q = OrientationMapper.RotatePoint(new OcrPoint(10, 20), 90, 100, 50);
        Assert.Equal(new OcrPoint(30, 10), q);
    }

    [Fact]
    public void RotatePoint_180_maps_via_width_and_height()
    {
        var p = OrientationMapper.RotatePoint(new OcrPoint(10, 20), 180, 100, 50);
        Assert.Equal(new OcrPoint(90, 30), p);
    }

    [Fact]
    public void RotatePoint_270_cw_maps_via_width()
    {
        // (x,y) → (y, W − x).
        var p = OrientationMapper.RotatePoint(new OcrPoint(10, 20), 270, 100, 50);
        Assert.Equal(new OcrPoint(20, 90), p);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotatePoint_roundtrip_returns_original(int degrees)
    {
        // Rotating W×H by d, then the result frame by (360−d), must be the identity.
        int w = 640, h = 480;
        int rw = degrees is 90 or 270 ? h : w;
        int rh = degrees is 90 or 270 ? w : h;

        var original = new OcrPoint(123.25, 45.75);
        var rotated = OrientationMapper.RotatePoint(original, degrees, w, h);
        var back = OrientationMapper.RotatePoint(rotated, 360 - degrees, rw, rh);

        Assert.Equal(original.X, back.X, 9);
        Assert.Equal(original.Y, back.Y, 9);
    }

    [Fact]
    public void RotatePoint_rejects_non_right_angles()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OrientationMapper.RotatePoint(new OcrPoint(0, 0), 45, 10, 10));
    }

    // ---- OrientationMapper: quad mapback (working frame → original frame) -----------------------------

    [Fact]
    public void MapLinesToOriginalFrame_180_restores_original_quad()
    {
        // Original page 200×100 was rotated 180° to upright it (working frame also 200×100). A quad at
        // the working frame's top-left maps back to the original's bottom-right, corner for corner.
        var workingQuad = new[]
        {
            new OcrPoint(10, 20), new OcrPoint(60, 20),
            new OcrPoint(60, 40), new OcrPoint(10, 40),
        };
        var line = MakeLine("hello", workingQuad);

        var mapped = OrientationMapper.MapLinesToOriginalFrame(new[] { line }, 180, 200, 100);

        var poly = mapped[0].BoundingPolygon;
        Assert.Equal(new OcrPoint(190, 80), poly[0]);
        Assert.Equal(new OcrPoint(140, 80), poly[1]);
        Assert.Equal(new OcrPoint(140, 60), poly[2]);
        Assert.Equal(new OcrPoint(190, 60), poly[3]);
        // The recomputed axis-aligned box covers the same area, reflected.
        Assert.Equal(140, mapped[0].BoundingBox.MinX, 9);
        Assert.Equal(60, mapped[0].BoundingBox.MinY, 9);
        Assert.Equal(190, mapped[0].BoundingBox.MaxX, 9);
        Assert.Equal(80, mapped[0].BoundingBox.MaxY, 9);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void MapLinesToOriginalFrame_90_and_270_roundtrip(int applied)
    {
        // Original 300×200 rotated `applied`° CW → working frame is 200×300. Mapping a working-frame
        // quad back and then forward again must reproduce it exactly.
        int workW = 200, workH = 300;
        var workingQuad = new[]
        {
            new OcrPoint(15, 25), new OcrPoint(80, 25),
            new OcrPoint(80, 45), new OcrPoint(15, 45),
        };
        var line = MakeLine("q", workingQuad);

        var mapped = OrientationMapper.MapLinesToOriginalFrame(new[] { line }, applied, workW, workH);
        // Every mapped point must land inside the ORIGINAL 300×200 frame.
        foreach (var p in mapped[0].BoundingPolygon)
        {
            Assert.InRange(p.X, 0, 300);
            Assert.InRange(p.Y, 0, 200);
        }

        var forward = OrientationMapper.RotatePolygon(mapped[0].BoundingPolygon, applied, 300, 200);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(workingQuad[i].X, forward[i].X, 9);
            Assert.Equal(workingQuad[i].Y, forward[i].Y, 9);
        }
    }

    [Fact]
    public void MapLinesToOriginalFrame_zero_rotation_returns_same_list()
    {
        var line = MakeLine("x", new[] { new OcrPoint(1, 2), new OcrPoint(3, 2), new OcrPoint(3, 4), new OcrPoint(1, 4) });
        var lines = new[] { line };
        Assert.Same(lines, OrientationMapper.MapLinesToOriginalFrame(lines, 0, 10, 10));
    }

    [SkippableTheory]
    [Trait("Category", "Integration")]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    public async Task Synthetic_page_returns_text_and_word_and_line_boxes_in_original_frame(
        int pageRotation, bool detectOrientation)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("PADDLEOCRNET_RUN_INTEGRATION") == "1",
            "Set PADDLEOCRNET_RUN_INTEGRATION=1 to run model-backed orientation checks.");

        using var upright = Image.Load<Rgb24>(SyntheticPagePath());
        using var rotated = ImagePreprocessor.RotateRightAngle(upright, pageRotation);
        // A caller that has already corrected the rotation supplies an upright image, not the scan.
        using var corrected = ImagePreprocessor.RotateRightAngle(rotated, (360 - pageRotation) % 360);
        var input = detectOrientation ? rotated : corrected;
        using var service = new PaddleOcrService();
        var options = new RecognitionOptions { ReturnWordBoxes = true, UseDocOrientation = false };
        var baseline = await service.ExtractTextFromImage(upright, OcrLanguage.English, options);
        AssertSyntheticLayout(baseline);

        var result = await service.ExtractTextFromImage(input, OcrLanguage.English,
            options with { UseDocOrientation = detectOrientation });
        int expectedRotation = detectOrientation ? pageRotation : 0;
        Assert.Equal(expectedRotation, result.DetectedOrientation);
        Assert.Equal(input.Width, result.SourceWidth);
        Assert.Equal(input.Height, result.SourceHeight);
        Assert.Equal(baseline.FullText, result.FullText);
        Assert.Equal(baseline.Lines.Count, result.Lines.Count);
        for (int i = 0; i < baseline.Lines.Count; i++)
        {
            var expected = baseline.Lines[i];
            var actual = result.Lines[i];
            Assert.Equal(expected.Text, actual.Text);
            AssertRotatedGeometry(expected.BoundingPolygon, actual.BoundingPolygon, actual.BoundingBox, expectedRotation);
            Assert.Equal(expected.Words.Select(w => w.Text), actual.Words.Select(w => w.Text));
            Assert.Equal(expected.Words.Count, actual.Words.Count);
            for (int j = 0; j < expected.Words.Count; j++)
                AssertRotatedGeometry(expected.Words[j].BoundingPolygon, actual.Words[j].BoundingPolygon,
                    actual.Words[j].BoundingBox, expectedRotation);
        }
    }

    private static string SyntheticPagePath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PaddleOcrNet.sln")))
            root = root.Parent;
        return Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "test", "Assets", "orientation-mapback.png");
    }

    private static void AssertSyntheticLayout(OcrResult result)
    {
        // Generated on a white 1200×800 canvas with Arial 40px, using typographic DrawString.
        // Detector expansion allows 20px; CTC boundaries allow 30px (less than one glyph at this size).
        // Rotation comparisons below remain tight at 3px for every polygon vertex and box edge.
        var layout = new[]
        {
            ("Clear morning", 90d, 75d, 341d, 120d, 186d),
            ("Bright paper", 180d, 190d, 398d, 235d, 284d),
            ("Simple words", 110d, 330d, 350d, 375d, 232d),
            ("Final reading", 250d, 510d, 481d, 555d, 337d),
        };
        Assert.Equal(layout.Select(l => l.Item1), result.Lines.Select(l => l.Text));
        for (int i = 0; i < layout.Length; i++)
        {
            var (text, x, y, right, bottom, split) = layout[i];
            var line = result.Lines[i];
            AssertBoxNear(new OcrBoundingBox(x, y, right, bottom), line.BoundingBox, 20);
            Assert.Equal(text.Split(' '), line.Words.Select(w => w.Text));
            AssertBoxNear(new OcrBoundingBox(x, y, split, bottom), line.Words[0].BoundingBox, 30);
            AssertBoxNear(new OcrBoundingBox(split, y, right, bottom), line.Words[1].BoundingBox, 30);
        }
    }

    private static void AssertRotatedGeometry(IReadOnlyList<OcrPoint> upright, IReadOnlyList<OcrPoint> actual,
        OcrBoundingBox actualBox, int rotation)
    {
        // Independent corner-coordinate oracle, deliberately not OrientationMapper.
        var expected = upright.Select(p => rotation switch
        {
            0 => p,
            90 => new OcrPoint(800 - p.Y, p.X),
            180 => new OcrPoint(1200 - p.X, 800 - p.Y),
            270 => new OcrPoint(p.Y, 1200 - p.X),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation)),
        }).ToArray();
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.InRange(Math.Abs(expected[i].X - actual[i].X), 0, 3);
            Assert.InRange(Math.Abs(expected[i].Y - actual[i].Y), 0, 3);
        }
        AssertBoxNear(OcrBoundingBox.FromPoints(expected), actualBox, 3);
    }

    private static void AssertBoxNear(OcrBoundingBox expected, OcrBoundingBox actual, double tolerance)
    {
        Assert.InRange(Math.Abs(expected.MinX - actual.MinX), 0, tolerance);
        Assert.InRange(Math.Abs(expected.MinY - actual.MinY), 0, tolerance);
        Assert.InRange(Math.Abs(expected.MaxX - actual.MaxX), 0, tolerance);
        Assert.InRange(Math.Abs(expected.MaxY - actual.MaxY), 0, tolerance);
    }

    // ---- RecognitionOptions / OcrResult defaults ------------------------------------------------------

    [Fact]
    public void RecognitionOptions_doc_orientation_defaults_match_documented_stance()
    {
        // Python parity has BOTH stages on; C# keeps orientation on and unwarp off (documented deviation).
        var options = new RecognitionOptions();
        Assert.True(options.UseDocOrientation);
        Assert.False(options.UseDocUnwarp);
        Assert.True(RecognitionOptions.Default.UseDocOrientation);
        Assert.False(RecognitionOptions.Default.UseDocUnwarp);
    }

    [Fact]
    public void OcrResult_detected_orientation_defaults_to_zero()
    {
        Assert.Equal(0, OcrResult.Empty.DetectedOrientation);
    }

    // ---- SortedBoxes (python sorted_boxes, used by the parity emitter) --------------------------------

    [Fact]
    public void SortedBoxes_SortLines_same_row_within_tolerance_reads_left_to_right()
    {
        // Y differs by 8 (< 10px tolerance): left box first despite being listed second.
        var right = MakeLine("right", Quad(100, 8, 40, 20));
        var left = MakeLine("left", Quad(10, 0, 40, 20));

        var sorted = SortedBoxes.SortLines(new[] { right, left });

        Assert.Equal(new[] { "left", "right" }, sorted.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void SortedBoxes_SortLines_beyond_tolerance_keeps_top_to_bottom()
    {
        // Y differs by 40 (>= 10): top row stays first regardless of X (the else-break path).
        var top = MakeLine("top", Quad(100, 0, 40, 20));
        var bottom = MakeLine("bottom", Quad(10, 40, 40, 20));

        var sorted = SortedBoxes.SortLines(new[] { bottom, top });

        Assert.Equal(new[] { "top", "bottom" }, sorted.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void SortedBoxes_SortLines_does_not_mutate_input()
    {
        var a = MakeLine("a", Quad(100, 0, 40, 20));
        var b = MakeLine("b", Quad(10, 0, 40, 20));
        var input = new List<OcrLine> { a, b };

        SortedBoxes.SortLines(input);

        Assert.Same(a, input[0]);
        Assert.Same(b, input[1]);
    }

    // ---- Rotation-aware reading order (service) -------------------------------------------------------

    [Fact]
    public void SortLinesByReadingOrder_with_180_rotation_sorts_in_uprighted_frame()
    {
        // Page 200×100 was read after a 180° upright; quads are mapped back to the ORIGINAL frame, where
        // the FIRST logical line sits at the BOTTOM. A naive original-frame sort would reverse the text.
        var first = MakeLine("first", Quad(140, 70, 50, 20));   // logical line 1 (bottom-right of original)
        var second = MakeLine("second", Quad(10, 10, 50, 20));  // logical line 2 (top-left of original)

        var naive = PaddleOcrService.SortLinesByReadingOrder(new[] { first, second });
        Assert.Equal(new[] { "second", "first" }, naive.Select(l => l.Text).ToArray());

        var aware = PaddleOcrService.SortLinesByReadingOrder(new[] { first, second }, appliedRotation: 180, sourceWidth: 200, sourceHeight: 100);
        Assert.Equal(new[] { "first", "second" }, aware.Select(l => l.Text).ToArray());
        // Coordinates themselves stay in the original frame — only the sort key was rotated.
        Assert.Equal(140, aware[0].BoundingBox.MinX, 9);
    }

    [Fact]
    public void SortLinesByReadingOrder_with_zero_rotation_matches_plain_overload()
    {
        var lines = new[]
        {
            MakeLine("b", Quad(0, 40, 50, 20)),
            MakeLine("a", Quad(0, 0, 50, 20)),
        };
        var plain = PaddleOcrService.SortLinesByReadingOrder(lines);
        var aware = PaddleOcrService.SortLinesByReadingOrder(lines, 0, 100, 100);
        Assert.Equal(plain.Select(l => l.Text), aware.Select(l => l.Text));
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static OcrPoint[] Quad(double x, double y, double w, double h) => new[]
    {
        new OcrPoint(x, y), new OcrPoint(x + w, y),
        new OcrPoint(x + w, y + h), new OcrPoint(x, y + h),
    };

    private static OcrLine MakeLine(string text, OcrPoint[] quad) => new()
    {
        Text = text,
        Confidence = 1.0,
        BoundingPolygon = quad,
        BoundingBox = OcrBoundingBox.FromPoints(quad),
    };
}
