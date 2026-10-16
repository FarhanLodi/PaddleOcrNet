using System.Text.Json;
using PaddleOcrNet.Models;
using PaddleOcrNet.Structure;
using Xunit;

namespace PaddleOcrNet.Tests;

public class DocumentAssemblyTests
{
    private static DocumentPage Page(int number, params StructureBlock[] blocks) => new()
    {
        PageNumber = number, Structure = new() { Blocks = blocks, SourceWidth = 1000, SourceHeight = 1000 },
    };
    private static StructureBlock Block(StructureBlockType type, int order, OcrBoundingBox box, string? text = null)
        => new(type, box, order, text);

    [Fact]
    public void Assembly_preserves_pages_orphans_and_existing_rtl_order_in_json()
    {
        var right = Block(StructureBlockType.Text, 0, new(600, 100, 900, 200), "يمين");
        var left = Block(StructureBlockType.Text, 1, new(100, 100, 400, 200), "يسار");
        var orphan = new OcrLine { Text = "unassigned footnote", BoundingBox = new(20, 900, 300, 920) };
        var input = Page(7, left, right) with { OrphanLines = new[] { orphan } };
        var document = DocumentAssembler.Assemble(new[] { input });
        Assert.Same(input, Assert.Single(document.Pages));
        var sequence = Assert.Single(document.Relationships);
        Assert.Equal(new DocumentBlockReference(7, 1), sequence.From);
        Assert.Equal(new DocumentBlockReference(7, 0), sequence.To);
        using var json = JsonDocument.Parse(document.ToJson());
        Assert.Equal("unassigned footnote", json.RootElement.GetProperty("Pages")[0].GetProperty("OrphanLines")[0].GetProperty("Text").GetString());
        Assert.Contains("يمين", document.ToJson());
    }

    [Fact]
    public void Containment_selects_smallest_container_without_discarding_overlapping_blocks()
    {
        var outer = Block(StructureBlockType.Table, 0, new(0, 0, 900, 900));
        var cell = Block(StructureBlockType.Text, 1, new(10, 10, 200, 200));
        var formula = Block(StructureBlockType.Formula, 2, new(20, 20, 100, 100));
        var doc = DocumentAssembler.Assemble(new[] { Page(1, outer, cell, formula) });
        Assert.Equal(3, doc.Pages[0].Structure.Blocks.Count);
        var parent = Assert.Single(doc.Relationships, r => r.Kind == DocumentRelationshipKind.Contains && r.To.BlockIndex == 2);
        Assert.Equal(1, parent.From.BlockIndex);
        Assert.True(parent.RequiresReview);
    }

    [Fact]
    public void Repeated_headers_are_evidence_only_and_body_repetition_is_not_a_header()
    {
        var header = Block(StructureBlockType.Header, 0, new(100, 10, 500, 40), "Report 2026");
        var body = Block(StructureBlockType.Text, 1, new(100, 300, 500, 340), "Report 2026");
        var doc = DocumentAssembler.Assemble(new[] { Page(1, header, body), Page(2, header, body) });
        Assert.Single(doc.Relationships, r => r.Kind == DocumentRelationshipKind.RepeatedHeader);
        Assert.All(doc.Pages, p => Assert.Equal(2, p.Structure.Blocks.Count));
    }

    [Fact]
    public void Adjacent_tables_need_boundary_alignment_and_matching_column_count_and_are_never_silently_merged()
    {
        const string html = "<table><tr><th>Name</th><th>Value</th></tr><tr><td>A</td><td>1</td></tr></table>";
        var bottom = Block(StructureBlockType.Table, 0, new(100, 500, 900, 950)) with { TableHtml = html };
        var top = bottom with { Bounds = new(100, 20, 900, 450) };
        var doc = DocumentAssembler.Assemble(new[] { Page(1, bottom), Page(2, top), Page(4, top) });
        var candidate = Assert.Single(doc.Relationships, r => r.Kind == DocumentRelationshipKind.TableContinuationCandidate);
        Assert.True(candidate.RequiresReview);
        Assert.Contains("identical first-row text", candidate.Evidence);
        Assert.Equal(3, doc.Pages.Count);
        Assert.Equal(html, doc.Pages[1].Structure.Blocks[0].TableHtml);
        Assert.Empty(DocumentAssembler.Assemble(new[] { Page(1, bottom), Page(2, top with { Bounds = new(400, 20, 950, 450) }) }).Relationships);
    }

    [Fact]
    public void Duplicate_page_numbers_are_rejected()
        => Assert.Throws<ArgumentException>(() => DocumentAssembler.Assemble(new[] { Page(1), Page(1) }));

    [Theory]
    [InlineData(0, false, true, true)]
    [InlineData(90, false, false, true)]
    [InlineData(0, true, false, false)]
    [InlineData(270, true, false, false)]
    public void Structure_frame_history_is_explicit_in_compact_and_full_json(int rotation, bool unwarp, bool original, bool inverse)
    {
        var coordinates = new StructureCoordinateProvenance { OriginalWidth = 1200, OriginalHeight = 800,
            RotationApplied = rotation, NonlinearDewarpApplied = unwarp };
        var structure = new StructureResult { Blocks = Array.Empty<StructureBlock>(), Coordinates = coordinates };
        Assert.Equal(original, coordinates.IsOriginalFrame);
        Assert.Equal(inverse, coordinates.InverseMappingAvailable);
        using var compact = JsonDocument.Parse(structure.ToJson());
        Assert.Equal(rotation, compact.RootElement.GetProperty("Coordinates").GetProperty("RotationApplied").GetInt32());
        var document = DocumentAssembler.Assemble(new[] { new DocumentPage { PageNumber = 1, Structure = structure } });
        using var full = JsonDocument.Parse(document.ToJson());
        Assert.Equal(unwarp, full.RootElement.GetProperty("Pages")[0].GetProperty("Structure").GetProperty("Coordinates").GetProperty("NonlinearDewarpApplied").GetBoolean());
    }
}
