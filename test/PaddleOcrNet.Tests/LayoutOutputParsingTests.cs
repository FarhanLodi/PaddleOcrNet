using PaddleOcrNet.Models;
using PaddleOcrNet.Structure;
using PaddleOcrNet.Structure.Layout;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for layout-detector post-processing: parsing the RT-DETR
/// <c>[N, 6]</c> output rows (<c>class_id, score, x1, y1, x2, y2</c>) with a score filter, and the
/// raw-class-id → <see cref="StructureBlockType"/> mapping for both the 23-class (PP-DocLayout_plus) and the
/// 20-class (PP-DocLayout) label vocabularies. The class mapping is exercised through the real, already-
/// implemented <see cref="LayoutLabelMap"/>; the <c>[N,6]</c> row parse is pinned via a local reference that
/// mirrors the post-processing the detector body must perform.
/// </summary>
public class LayoutOutputParsingTests
{
    // -----------------------------------------------------------------------------------------------------
    // [N, 6] output parse + score filter
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Parses an RT-DETR <c>[N, 6]</c> output buffer (<c>class_id, score, x1, y1, x2, y2</c>) into regions,
    /// dropping any row whose score is below <paramref name="scoreThreshold"/> and mapping the class id via
    /// <paramref name="classMap"/>. This is the post-processing the detector body must perform.
    /// </summary>
    private static IReadOnlyList<LayoutRegion> ParseDetections(
        float[,] output, float scoreThreshold, IReadOnlyDictionary<int, StructureBlockType> classMap)
    {
        int n = output.GetLength(0);
        var regions = new List<LayoutRegion>(n);
        for (int i = 0; i < n; i++)
        {
            int classId = (int)output[i, 0];
            float score = output[i, 1];
            if (score < scoreThreshold) continue;

            var bounds = new OcrBoundingBox(output[i, 2], output[i, 3], output[i, 4], output[i, 5]);
            var type = classMap.TryGetValue(classId, out var mapped) ? mapped : StructureBlockType.Unknown;
            regions.Add(new LayoutRegion(type, bounds, score, classId));
        }
        return regions;
    }

    [Fact]
    public void ParseDetections_drops_rows_below_the_score_threshold()
    {
        var classMap = new Dictionary<int, StructureBlockType>
        {
            [0] = StructureBlockType.Text,
            [1] = StructureBlockType.Title,
            [2] = StructureBlockType.Table,
        };

        // Three rows; the middle one (score 0.20) is below the 0.50 threshold and must be dropped.
        var output = new float[,]
        {
            { 0, 0.95f, 10, 10, 110, 50 },
            { 1, 0.20f, 10, 60, 110, 90 },
            { 2, 0.80f, 10, 100, 210, 300 },
        };

        var regions = ParseDetections(output, scoreThreshold: 0.5f, classMap);

        Assert.Equal(2, regions.Count);
        Assert.Equal(StructureBlockType.Text, regions[0].Type);
        Assert.Equal(StructureBlockType.Table, regions[1].Type);
    }

    [Fact]
    public void ParseDetections_maps_box_corners_score_and_raw_class_id()
    {
        var classMap = new Dictionary<int, StructureBlockType> { [7] = StructureBlockType.Figure };
        var output = new float[,] { { 7, 0.73f, 12, 34, 156, 278 } };

        var region = Assert.Single(ParseDetections(output, 0.5f, classMap));

        Assert.Equal(StructureBlockType.Figure, region.Type);
        Assert.Equal(7, region.RawClassId);
        Assert.Equal(0.73f, region.Score, 5);
        Assert.Equal(new OcrBoundingBox(12, 34, 156, 278), region.Bounds);
    }

    [Fact]
    public void ParseDetections_unknown_class_id_falls_back_to_Unknown()
    {
        var classMap = new Dictionary<int, StructureBlockType> { [0] = StructureBlockType.Text };
        var output = new float[,] { { 99, 0.99f, 0, 0, 10, 10 } };

        var region = Assert.Single(ParseDetections(output, 0.5f, classMap));

        Assert.Equal(StructureBlockType.Unknown, region.Type);
        Assert.Equal(99, region.RawClassId);
    }

    // -----------------------------------------------------------------------------------------------------
    // class-id -> StructureBlockType mapping (real LayoutLabelMap) — 23-class and 20-class label sets
    // -----------------------------------------------------------------------------------------------------

    // PP-DocLayout_plus-L (RT-DETR), 23 classes in class-id order.
    private static readonly string[] Labels23 =
    {
        "paragraph_title", "image", "text", "number", "abstract",
        "content", "figure_title", "formula", "table", "table_title",
        "reference", "doc_title", "footnote", "header", "algorithm",
        "footer", "seal", "chart_title", "chart", "formula_number",
        "header_image", "footer_image", "aside_text",
    };

    // PP-DocLayout-S/M (PicoDet), 20 classes in class-id order.
    private static readonly string[] Labels20 =
    {
        "text", "title", "figure", "figure_caption", "table",
        "table_caption", "header", "footer", "reference", "equation",
        "list", "abstract", "content", "paragraph_title", "doc_title",
        "footnote", "page_number", "seal", "chart", "formula_number",
    };

    private static IReadOnlyDictionary<int, StructureBlockType> BuildMap(string[] labels)
    {
        var map = new Dictionary<int, StructureBlockType>(labels.Length);
        for (int id = 0; id < labels.Length; id++)
            map[id] = LayoutLabelMap.MapName(labels[id]);
        return map;
    }

    [Fact]
    public void MapName_resolves_the_23_class_plus_label_set()
    {
        var map = BuildMap(Labels23);

        Assert.Equal(StructureBlockType.Title, map[0]);    // paragraph_title
        Assert.Equal(StructureBlockType.Figure, map[1]);   // image
        Assert.Equal(StructureBlockType.Text, map[2]);     // text
        Assert.Equal(StructureBlockType.Formula, map[7]);  // formula
        Assert.Equal(StructureBlockType.Table, map[8]);    // table
        Assert.Equal(StructureBlockType.DocTitle, map[11]); // doc_title
        Assert.Equal(StructureBlockType.Seal, map[16]);    // seal
        Assert.Equal(StructureBlockType.Chart, map[18]);   // chart
        Assert.Equal(StructureBlockType.FormulaNumber, map[19]); // formula_number
        Assert.Equal(StructureBlockType.Aside, map[22]);   // aside_text
    }

    [Fact]
    public void MapName_resolves_the_20_class_label_set()
    {
        var map = BuildMap(Labels20);

        Assert.Equal(StructureBlockType.Text, map[0]);          // text
        Assert.Equal(StructureBlockType.Title, map[1]);         // title
        Assert.Equal(StructureBlockType.Figure, map[2]);        // figure
        Assert.Equal(StructureBlockType.FigureCaption, map[3]); // figure_caption
        Assert.Equal(StructureBlockType.Table, map[4]);         // table
        Assert.Equal(StructureBlockType.TableCaption, map[5]);  // table_caption
        Assert.Equal(StructureBlockType.Header, map[6]);        // header
        Assert.Equal(StructureBlockType.Footer, map[7]);        // footer
        Assert.Equal(StructureBlockType.Formula, map[9]);       // equation
        Assert.Equal(StructureBlockType.List, map[10]);         // list
        Assert.Equal(StructureBlockType.PageNumber, map[16]);   // page_number
        Assert.Equal(StructureBlockType.Seal, map[17]);         // seal
    }

    [Fact]
    public void MapName_is_case_and_separator_insensitive()
    {
        Assert.Equal(StructureBlockType.Text, LayoutLabelMap.MapName("Plain-Text"));
        Assert.Equal(StructureBlockType.Title, LayoutLabelMap.MapName("Section Title"));
        Assert.Equal(StructureBlockType.Formula, LayoutLabelMap.MapName("INTERLINE_EQUATION"));
    }

    [Fact]
    public void MapName_unknown_label_maps_to_Unknown()
    {
        Assert.Equal(StructureBlockType.Unknown, LayoutLabelMap.MapName("totally_made_up_label"));
    }
}
