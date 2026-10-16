using System.Text.Json;
using PaddleOcrNet.Models;
using PaddleOcrNet.Structure;
using Xunit;

namespace PaddleOcrNet.Tests;

public class DocumentVerificationTests
{
    private static DocumentStructure Document(params string[] values) => DocumentAssembler.Assemble(new[]
    {
        new DocumentPage { PageNumber = 1, Structure = new StructureResult
        {
            SourceWidth = 1000, SourceHeight = 1000,
            Coordinates = new StructureCoordinateProvenance { OriginalWidth = 1000, OriginalHeight = 1000 },
            Blocks = values.Select((text, i) => new StructureBlock(StructureBlockType.Text,
                new OcrBoundingBox(10, 10 + i * 30, 110, 30 + i * 30), i, Text: text)).ToArray(),
        } },
    });
    private static DocumentVerificationRule Sum(string id = "invoice-total", string culture = "") => new()
    {
        Id = id, Kind = DocumentVerificationKind.DecimalSum, Inputs = new[] { new DocumentBlockReference(1, 0), new DocumentBlockReference(1, 1) },
        Expected = new(1, 2), CultureName = culture,
    };

    [Fact]
    public void Incorrect_printed_total_is_preserved_and_requests_all_source_regions_for_review()
    {
        var document = Document("10.25", "20.50", "99.99");
        var report = DocumentVerifier.Verify(document, new[] { Sum() });
        var check = Assert.Single(report.Checks);
        Assert.Equal(DocumentVerificationStatus.Contradiction, check.Status);
        Assert.Equal("99.99", check.Readings[^1].RawText);
        Assert.Equal("99.99", document.Pages[0].Structure.Blocks[2].Text);
        Assert.Equal(3, report.RequestedRereads.Count);
        Assert.Equal(6000, report.RequestedPixels);
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal("99.99", json.RootElement.GetProperty("Checks")[0].GetProperty("Readings")[2].GetProperty("RawText").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("RequestedRereads")[0].GetProperty("Source").GetProperty("PageNumber").GetInt32());
    }

    [Fact]
    public void Explicit_locale_and_tolerance_work_without_extracting_currency_or_guessing_group_separators()
    {
        var correct = DocumentVerifier.Verify(Document("10,25", "20,50", "30,75"), new[] { Sum(culture: "de-DE") });
        Assert.Equal(DocumentVerificationStatus.Consistent, Assert.Single(correct.Checks).Status);
        Assert.Empty(correct.RequestedRereads);
        foreach (string bad in new[] { "USD 10.25", "Total: 10.25", "1,025", "1e2" })
        {
            var report = DocumentVerifier.Verify(Document(bad, "20.50", "30.75"), new[] { Sum() });
            Assert.Equal(DocumentVerificationStatus.Unverifiable, Assert.Single(report.Checks).Status);
        }
        var tolerant = DocumentVerifier.Verify(Document("10.25", "20.50", "30.76"), new[] { Sum() with { AbsoluteTolerance = 0.01m } });
        Assert.Equal(DocumentVerificationStatus.Consistent, Assert.Single(tolerant.Checks).Status);
    }

    [Theory]
    [InlineData("79228162514264337593543950335", "1", "0", "decimal-overflow")]
    [InlineData("1.00000000000000000000000000001", "0", "1", "not-a-complete-decimal")]
    public void Overflow_and_unrepresentable_precision_do_not_produce_false_agreement(string a, string b, string total, string reason)
    {
        var check = Assert.Single(DocumentVerifier.Verify(Document(a, b, total), new[] { Sum() }).Checks);
        Assert.Equal(DocumentVerificationStatus.Unverifiable, check.Status);
        Assert.Equal(reason, check.Reason);
    }

    [Fact]
    public void Exact_unit_label_checks_preserve_case_whitespace_and_critical_tokens()
    {
        var rule = new DocumentVerificationRule { Id = "unit", Kind = DocumentVerificationKind.ExactText,
            Inputs = new[] { new DocumentBlockReference(1, 0) }, Expected = new(1, 1) };
        foreach (var values in new[] { new[] { "mW", "MW" }, new[] { "8:47", "8147" }, new[] { "AB", "A B" } })
            Assert.Equal(DocumentVerificationStatus.Contradiction,
                Assert.Single(DocumentVerifier.Verify(Document(values), new[] { rule }).Checks).Status);
    }

    [Fact]
    public void Rule_reference_and_reread_budgets_are_separate_and_requests_are_deduplicated()
    {
        var doc = Document("1", "2", "99");
        var report = DocumentVerifier.Verify(doc, new[] { Sum(), Sum("second"), Sum("third") },
            new DocumentVerificationPolicy { MaxRules = 2, MaxReferences = 3, MaxRereadRequests = 2, MaxRereadPixels = 2000 });
        Assert.True(report.RulesTruncated);
        Assert.Equal(DocumentVerificationStatus.NotEvaluated, report.Checks[1].Status);
        Assert.Single(report.RequestedRereads);
        Assert.Equal(2, report.SkippedRereads.Count);
        Assert.Equal(2000, report.RequestedPixels);
        var repeated = DocumentVerifier.Verify(doc, new[] { Sum(), Sum("second") });
        Assert.Equal(3, repeated.RequestedRereads.Count);
    }

    [Fact]
    public void Unmapped_rotated_or_unknown_coordinates_retain_evidence_without_source_rereads()
    {
        foreach (var provenance in new StructureCoordinateProvenance?[] { null,
            new() { RotationApplied = 90 }, new() { NonlinearDewarpApplied = true } })
        {
            var doc = Document("1", "2", "99");
            var original = doc.Pages[0];
            doc = doc with { Pages = new[] { original with { Structure = new StructureResult
            {
                Blocks = original.Structure.Blocks, SourceWidth = 1000, SourceHeight = 1000, Coordinates = provenance,
            } } } };
            var report = DocumentVerifier.Verify(doc, new[] { Sum() });
            Assert.Equal(DocumentVerificationStatus.Contradiction, Assert.Single(report.Checks).Status);
            Assert.Empty(report.RequestedRereads);
            Assert.All(report.SkippedRereads, r => Assert.Equal("source-frame-unavailable", r.Reason));
        }
    }

    [Fact]
    public void Missing_sources_are_review_evidence_and_cancelled_verification_stops_before_rule_enumeration()
    {
        var rule = Sum() with { Expected = new(2, 99) };
        var report = DocumentVerifier.Verify(Document("1", "2", "3"), new[] { rule });
        Assert.Equal("missing-source", Assert.Single(report.Checks).Reason);
        Assert.Contains(report.SkippedRereads, r => r.Reason == "missing-source");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => DocumentVerifier.Verify(Document("1", "2", "3"),
            new[] { rule }, cancellationToken: cts.Token));
    }

    [Fact]
    public void Duplicate_source_operands_are_rejected_instead_of_double_counting()
    {
        var rule = Sum() with { Inputs = new[] { new DocumentBlockReference(1, 0), new DocumentBlockReference(1, 0) } };
        Assert.Throws<ArgumentException>(() => DocumentVerifier.Verify(Document("1", "2", "2"), new[] { rule }));
    }
}
