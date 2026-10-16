using PaddleOcrNet.Models;
using PaddleOcrNet.Structure;
using Xunit;

namespace PaddleOcrNet.Tests;

public class DocumentTableJoiningTests
{
    private const string Header = "<tr><th>Name</th><th>First</th><th>Second</th></tr>";
    private const string Body = "<tr><td rowspan=\"2\">Group</td><td colspan=\"2\">Merged value</td></tr><tr><td>A</td><td>B</td></tr>";
    private const string Html = "<table>" + Header + Body + "</table>";

    private static DocumentPage Page(int number, string html, bool bottom) => new()
    {
        PageNumber = number,
        Structure = new StructureResult { SourceWidth = 1000, SourceHeight = 1000, Blocks = new[]
        {
            new StructureBlock(StructureBlockType.Table, bottom ? new OcrBoundingBox(100, 500, 900, 950) : new OcrBoundingBox(100, 20, 900, 450), 0, TableHtml: html),
        } },
    };
    private static DocumentStructure Document(string first = Html, string second = Html)
        => DocumentAssembler.Assemble(new[] { Page(1, first, true), Page(2, second, false) });
    private static DocumentRelationship Candidate(DocumentStructure document)
        => Assert.Single(document.Relationships, r => r.Kind == DocumentRelationshipKind.TableContinuationCandidate);

    [Fact]
    public void Explicit_approval_preserves_merged_cells_and_source_row_membership_in_separate_view()
    {
        var document = Document();
        Assert.Empty(document.CreateJoinedTables(Array.Empty<DocumentRelationship>()));
        var view = Assert.Single(document.CreateJoinedTables(new[] { Candidate(document) }));
        Assert.Equal(3, view.ColumnCount);
        Assert.Equal(5, view.Rows.Count);
        Assert.Equal(new[] { new DocumentBlockReference(1, 0), new DocumentBlockReference(2, 0) }, view.Sources);
        Assert.Equal(2, view.Rows[1].Cells[0].RowSpan);
        Assert.Equal(2, view.Rows[1].Cells[1].ColumnSpan);
        Assert.Equal(1, view.Rows[2].Cells[0].ColumnIndex);
        Assert.Equal(new DocumentBlockReference(2, 0), view.Rows[3].Source);
        Assert.Equal(1, view.Rows[3].SourceRowIndex);
        Assert.Equal(2, view.Rows[3].Cells[0].RowSpan);
        var omitted = Assert.Single(view.OmittedHeaders);
        Assert.Equal(new DocumentBlockReference(2, 0), omitted.Source);
        Assert.Equal(0, omitted.SourceRowIndex);
        Assert.Equal("Name", omitted.Cells[0].Text);
        using var json = System.Text.Json.JsonDocument.Parse(view.ToJson());
        Assert.Equal(2, json.RootElement.GetProperty("Rows")[3].GetProperty("Source").GetProperty("PageNumber").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("Rows")[1].GetProperty("Cells")[0].GetProperty("RowSpan").GetInt32());
        Assert.All(document.Pages, p => Assert.Equal(Html, p.Structure.Blocks[0].TableHtml));
    }

    [Fact]
    public void Header_mismatch_is_rejected_even_when_caller_confirms_geometric_candidate()
    {
        var document = Document(second: Html.Replace("Second", "Other", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => document.CreateJoinedTables(new[] { Candidate(document) }));
    }

    [Theory]
    [InlineData("<table><tr><th rowspan=\"2\">Name</th><th>First</th><th>Second</th></tr><tr><td>A</td><td>B</td></tr></table>")]
    [InlineData("<table><tr><th>Name</th><th>First</th><th>Second</th></tr><tr><td rowspan=\"5\">A</td><td>B</td><td>C</td></tr></table>")]
    [InlineData("<table><tr><th>Name</th><th>First</th><th>Second</th></tr><tr><td>A</td><td>B</td></tr></table>")]
    public void Ambiguous_spans_or_ragged_rows_are_rejected_without_flattening(string html)
    {
        var document = Document(second: html);
        Assert.Throws<ArgumentException>(() => document.CreateJoinedTables(new[] { Candidate(document) }));
    }

    [Fact]
    public void Duplicate_approvals_and_foreign_candidates_are_rejected()
    {
        var document = Document();
        var candidate = Candidate(document);
        Assert.Throws<ArgumentException>(() => document.CreateJoinedTables(new[] { candidate, candidate }));
        Assert.Throws<ArgumentException>(() => document.CreateJoinedTables(new[] { candidate with { To = new(3, 0) } }));
    }

    [Fact]
    public void Approved_chain_preserves_original_page_gaps_rule_and_omission_log()
    {
        var document = Document();
        var third = Page(3, Html, false);
        var link = Candidate(document) with { From = new(2, 0), To = new(3, 0) };
        document = document with { Pages = document.Pages.Append(third).ToArray(), Relationships = document.Relationships.Append(link).ToArray() };
        var view = Assert.Single(document.CreateJoinedTables(document.Relationships));
        Assert.Equal(7, view.Rows.Count);
        Assert.Equal(2, view.OmittedHeaders.Count);
        var gap = link with { To = new(4, 0) };
        var invalid = document with { Relationships = new[] { gap } };
        Assert.Throws<ArgumentException>(() => invalid.CreateJoinedTables(new[] { gap }));
    }

    [Fact]
    public void Malformed_markup_and_external_entities_are_not_repaired_or_resolved()
    {
        var document = Document();
        var candidate = Candidate(document);
        foreach (var markup in new[] { "<table><tr><td>broken", "<!DOCTYPE table [<!ENTITY x SYSTEM 'file:///unused'>]><table><tr><td>&x;</td></tr></table>" })
        {
            var edited = document with { Pages = new[] { Page(1, Html, true), Page(2, markup, false) } };
            Assert.Throws<ArgumentException>(() => edited.CreateJoinedTables(new[] { candidate }));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Invalid_row_hierarchy_span_attributes_and_row_group_crossings_are_rejected(int kind)
    {
        string markup = kind switch
        {
            0 => "<table><div>" + Header + Body + "</div></table>",
            1 => "<table>" + Header.Replace("<th>Name", "<th colspan=\"1\" COLSPAN=\"1\">Name", StringComparison.Ordinal) + Body + "</table>",
            2 => "<table>" + Header + "<tbody><tr><td rowspan=\"2\">C</td><td>D</td><td>E</td></tr></tbody><tbody><tr><td>F</td><td>G</td></tr></tbody></table>",
            _ => "<table>" + Header + Body.Replace("<tr>", "<tr>Unassigned text", StringComparison.Ordinal) + "</table>",
        };
        var document = Document();
        var candidate = Candidate(document);
        var edited = document with { Pages = new[] { Page(1, Html, true), Page(2, markup, false) } };
        Assert.Throws<ArgumentException>(() => edited.CreateJoinedTables(new[] { candidate }));
    }

    [Fact]
    public void Oversized_table_markup_is_rejected_before_parsing()
    {
        var document = Document();
        var candidate = Candidate(document);
        var edited = document with { Pages = new[] { Page(1, Html, true), Page(2, Html + new string(' ', 4_000_001), false) } };
        Assert.Throws<ArgumentException>(() => edited.CreateJoinedTables(new[] { candidate }));
    }
}
