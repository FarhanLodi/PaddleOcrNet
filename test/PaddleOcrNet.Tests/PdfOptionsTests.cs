using PaddleOcrNet.Pdf;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no PDF, no model download, CI-safe) for the <see cref="PdfOcrOptions.PageRange"/>
/// mini-syntax parser (<see cref="PdfPageRange.Parse"/>) and the options plumbing for the new page-range and
/// password members. The parser turns a 1-based range string such as <c>"1-3,5,8-"</c> into the concrete set
/// of 1-based pages to render, clamped to the document's page count.
/// <para>
/// Encrypted-PDF rendering is NOT covered here: it needs a real password-protected PDF fixture and the native
/// PDFium runtime. Those paths are exercised by integration tests; this file pins only the parser and option
/// validation that can be checked deterministically.
/// </para>
/// </summary>
public class PdfOptionsTests
{
    private static int[] Parse(string? range, int pageCount) => PdfPageRange.Parse(range, pageCount).ToArray();

    [Fact]
    public void Null_or_blank_range_selects_every_page_in_order()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Parse(null, 5));
        Assert.Equal(new[] { 1, 2, 3 }, Parse("", 3));
        Assert.Equal(new[] { 1, 2, 3 }, Parse("   ", 3));
    }

    [Fact]
    public void Empty_document_yields_no_pages()
    {
        Assert.Empty(Parse(null, 0));
    }

    [Fact]
    public void Canonical_example_1_3_5_8_open_over_10_pages()
    {
        // "1-3,5,8-" over a 10-page doc -> 1,2,3,5,8,9,10.
        Assert.Equal(new[] { 1, 2, 3, 5, 8, 9, 10 }, Parse("1-3,5,8-", 10));
    }

    [Fact]
    public void Single_pages_are_collected_and_sorted_and_deduplicated()
    {
        Assert.Equal(new[] { 1, 4, 7 }, Parse("7,1,4,1,7", 10));
    }

    [Fact]
    public void Closed_range_is_inclusive_of_both_ends()
    {
        Assert.Equal(new[] { 2, 3, 4 }, Parse("2-4", 10));
    }

    [Fact]
    public void Open_ended_start_means_from_page_one()
    {
        Assert.Equal(new[] { 1, 2, 3 }, Parse("-3", 10));
    }

    [Fact]
    public void Open_ended_end_runs_to_the_last_page()
    {
        Assert.Equal(new[] { 8, 9, 10 }, Parse("8-", 10));
    }

    [Fact]
    public void Overlapping_terms_are_merged_into_a_distinct_set()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Parse("1-3,2-5,4", 10));
    }

    [Fact]
    public void Whitespace_around_terms_and_dashes_is_ignored()
    {
        Assert.Equal(new[] { 1, 2, 3, 5 }, Parse("  1 - 3 , 5 ", 10));
    }

    [Fact]
    public void Pages_beyond_the_document_are_clamped_away()
    {
        // Doc has 5 pages: a single page past the end contributes nothing...
        Assert.Empty(Parse("99", 5));
        // ...an open-ended range stops at the last page...
        Assert.Equal(new[] { 3, 4, 5 }, Parse("3-", 5));
        // ...and a closed range partly outside keeps only its in-range part.
        Assert.Equal(new[] { 4, 5 }, Parse("4-100", 5));
    }

    [Fact]
    public void Selection_entirely_outside_the_document_is_empty()
    {
        Assert.Empty(Parse("20-30", 5));
    }

    [Theory]
    [InlineData("0")]          // page numbers are 1-based; 0 is invalid
    [InlineData("1-0")]        // end < 1
    [InlineData("5-2")]        // reversed range
    [InlineData("abc")]        // non-numeric
    [InlineData("1-a")]        // non-numeric end
    [InlineData("1,,3")]       // empty term (double comma)
    [InlineData(",1")]         // leading comma
    [InlineData("1,")]         // trailing comma
    [InlineData("-")]          // bare dash
    [InlineData("1-2-3")]      // too many dashes
    [InlineData(" 1.5 ")]      // non-integer (NumberStyles.None rejects the decimal point)
    public void Invalid_syntax_throws_argument_exception(string range)
    {
        Assert.Throws<ArgumentException>(() => PdfPageRange.Parse(range, 10));
    }

    [Fact]
    public void Leading_dash_with_single_page_is_a_valid_open_start_range()
    {
        // "-1" is open-start: pages 1..1.
        Assert.Equal(new[] { 1 }, Parse("-1", 10));
    }

    [Fact]
    public void Validate_rejects_malformed_range_syntax()
    {
        var options = new PdfOcrOptions { PageRange = "5-2" };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_accepts_a_wellformed_range_and_a_password()
    {
        var options = new PdfOcrOptions { PageRange = "1-3,5,8-", Password = "secret" };
        options.Validate(); // must not throw
        Assert.True(options.HasPageRange);
        Assert.Equal("secret", options.Password);
    }

    [Fact]
    public void HasPageRange_is_false_when_unset_or_blank()
    {
        Assert.False(new PdfOcrOptions().HasPageRange);
        Assert.False(new PdfOcrOptions { PageRange = "   " }.HasPageRange);
        Assert.True(new PdfOcrOptions { PageRange = "1" }.HasPageRange);
    }

    [Fact]
    public void Password_defaults_to_null()
    {
        Assert.Null(new PdfOcrOptions().Password);
    }

    [Fact]
    public void ParseTerms_validates_syntax_without_expanding_open_ended_ranges()
    {
        // An open-ended "8-" must NOT be expanded here (no page count is known yet): it stays a single term
        // with a null end. This is what lets PdfOcrOptions.Validate() check syntax cheaply, with no risk of
        // materializing billions of pages.
        var terms = PdfPageRange.ParseTerms("1-3,5,8-");

        Assert.Collection(terms,
            t => Assert.Equal(new PdfPageRange.Term(1, 3), t),
            t => Assert.Equal(new PdfPageRange.Term(5, 5), t),
            t => Assert.Equal(new PdfPageRange.Term(8, null), t));
    }

    [Fact]
    public void ParseTerms_returns_empty_for_null_or_blank()
    {
        Assert.Empty(PdfPageRange.ParseTerms(null));
        Assert.Empty(PdfPageRange.ParseTerms("  "));
    }
}
