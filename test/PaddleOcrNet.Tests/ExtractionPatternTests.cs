using System.Text.RegularExpressions;
using PaddleOcrNet.Extraction;
using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-free tests for <see cref="OcrMatchExtensions.FindMatches(OcrResult, OcrPattern)"/> and the built-in
/// <see cref="OcrPatterns"/>: hand-built results, deterministic boxes.
/// </summary>
public class ExtractionPatternTests
{
    private static OcrLine Line(string text, double x1 = 0, double y1 = 0, double x2 = 100, double y2 = 20, double confidence = 0.9) => new()
    {
        Text = text,
        Confidence = confidence,
        BoundingBox = new OcrBoundingBox(x1, y1, x2, y2),
    };

    private static OcrResult Result(params OcrLine[] lines) => new()
    {
        FullText = string.Join('\n', lines.Select(l => l.Text)),
        Lines = lines,
        Languages = new[] { "en" },
    };

    private static IReadOnlyList<OcrMatch> Find(string text, OcrPattern pattern) => Result(Line(text)).FindMatches(pattern);

    [Fact]
    public void Regex_overload_reports_custom_kind_line_reference_and_proportional_box()
    {
        var line = Line("ab cd", 0, 10, 50, 30, confidence: 0.77);
        var result = Result(Line("nothing"), line);

        var match = Assert.Single(result.FindMatches(new Regex("cd")));

        Assert.Equal("cd", match.Value);
        Assert.Equal(OcrMatchKind.Custom, match.Kind);
        Assert.Null(match.Normalized);
        Assert.Same(line, match.Line);
        Assert.Equal(1, match.LineIndex);
        Assert.Equal(3, match.Index);
        Assert.Equal(2, match.Length);
        Assert.Equal(0.77, match.Confidence);
        Assert.Equal(new OcrBoundingBox(30, 10, 50, 30), match.BoundingBox);
    }

    [Fact]
    public void Box_estimate_counts_full_width_characters_double()
    {
        // "合计 12" = 4 + 1 + 2 = 7 display columns over 70 px.
        var match = Assert.Single(Result(Line("合计 12", 0, 0, 70, 20)).FindMatches(new Regex("12")));
        Assert.Equal(50, match.BoundingBox.MinX, 6);
        Assert.Equal(70, match.BoundingBox.MaxX, 6);
    }

    [Fact]
    public void Custom_pattern_applies_validator_and_normalizer()
    {
        var pattern = new OcrPattern(new Regex(@"INV-\d+"), validator: v => v.EndsWith('7'), normalizer: v => v[4..]);
        var matches = Find("INV-1007 INV-1008", pattern);

        var match = Assert.Single(matches);
        Assert.Equal("INV-1007", match.Value);
        Assert.Equal("1007", match.Normalized);
    }

    [Fact]
    public void Email_lowercases_domain_only()
    {
        var match = Assert.Single(Find("Contact: John.Doe@Example.COM today", OcrPatterns.Email));
        Assert.Equal("John.Doe@Example.COM", match.Value);
        Assert.Equal("John.Doe@example.com", match.Normalized);
        Assert.Equal(OcrMatchKind.Email, match.Kind);
        Assert.Equal(9, match.Index);
    }

    [Theory]
    [InlineData("Visit https://example.com/path.", "https://example.com/path")]
    [InlineData("See www.paddle.org, thanks", "www.paddle.org")]
    public void Url_excludes_trailing_punctuation(string text, string expected)
    {
        Assert.Equal(expected, Assert.Single(Find(text, OcrPatterns.Url)).Value);
    }

    [Theory]
    [InlineData("Tel +1 (555) 123-4567", "+15551234567")]
    [InlineData("Mobile: +49 (0) 151 1234 5678", "+4915112345678")]
    [InlineData("Office (020) 7946 0958", "02079460958")]
    [InlineData("Call 555-123-4567 now", "5551234567")]
    public void Phone_matches_and_normalizes(string text, string expected)
    {
        Assert.Equal(expected, Assert.Single(Find(text, OcrPatterns.Phone)).Normalized);
    }

    [Theory]
    [InlineData("Invoice 12.345.678.901")]
    [InlineData("Date 12-03-2026")]
    [InlineData("ID 1 234 567")]
    [InlineData("Qty 42")]
    [InlineData("Card 4111 1111 1111 1111")]
    public void Phone_rejects_numbers_that_are_not_phones(string text)
    {
        Assert.Empty(Find(text, OcrPatterns.Phone));
    }

    [Theory]
    [InlineData("Issued 12 Mar 2026", "12 Mar 2026", "2026-03-12")]
    [InlineData("Date: 2026-03-12", "2026-03-12", "2026-03-12")]
    [InlineData("March 5th, 2026", "March 5th, 2026", "2026-03-05")]
    [InlineData("on 25/12/2026", "25/12/2026", "2026-12-25")]
    [InlineData("on 12/25/2026", "12/25/2026", "2026-12-25")]
    [InlineData("due 7-JUNE-2026", "7-JUNE-2026", "2026-06-07")]
    public void Date_normalizes_to_iso_when_unambiguous(string text, string value, string iso)
    {
        var match = Assert.Single(Find(text, OcrPatterns.Date));
        Assert.Equal(value, match.Value);
        Assert.Equal(iso, match.Normalized);
    }

    [Theory]
    [InlineData("03/04/2026")]
    [InlineData("12.03.26")]
    public void Ambiguous_dates_match_without_normalized_form(string text)
    {
        var match = Assert.Single(Find(text, OcrPatterns.Date));
        Assert.Null(match.Normalized);
    }

    [Theory]
    [InlineData("31/02/2026")]
    [InlineData("2026-13-01")]
    [InlineData("version 1.5.26")]
    public void Impossible_or_non_dates_are_rejected(string text)
    {
        Assert.Empty(Find(text, OcrPatterns.Date));
    }

    [Theory]
    [InlineData("Total: €1.234,56", "€1.234,56", "EUR 1234.56")]
    [InlineData("Amount $ 1,234.50", "$ 1,234.50", "$ 1234.50")]
    [InlineData("Pay USD 99 today", "USD 99", "USD 99")]
    [InlineData("Refund -£12.5", "-£12.5", "GBP -12.5")]
    [InlineData("Preis 12,50 €", "12,50 €", "EUR 12.50")]
    [InlineData("CHF 1'250.00", "CHF 1'250.00", "CHF 1250.00")]
    public void Amount_understands_symbols_codes_and_separators(string text, string value, string normalized)
    {
        var match = Assert.Single(Find(text, OcrPatterns.Amount));
        Assert.Equal(value, match.Value);
        Assert.Equal(normalized, match.Normalized);
    }

    [Theory]
    [InlineData("Qty 12")]
    [InlineData("EUR1,234,56")]
    public void Amount_requires_currency_and_consistent_separators(string text)
    {
        Assert.Empty(Find(text, OcrPatterns.Amount));
    }

    [Theory]
    [InlineData("1.234,56", "1234.56")]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("1'234.5", "1234.5")]
    [InlineData("0012", "12")]
    [InlineData("1,234", "1234")]
    [InlineData("1,234,56", null)]
    [InlineData("1.234.567,89", "1234567.89")]
    public void NormalizeNumber_resolves_decimal_and_grouping(string input, string? expected)
    {
        Assert.Equal(expected, OcrPatterns.NormalizeNumber(input));
    }

    [Theory]
    [InlineData("IBAN DE89 3704 0044 0532 0130 00", "DE89 3704 0044 0532 0130 00", "DE89370400440532013000")]
    [InlineData("GB82WEST12345698765432", "GB82WEST12345698765432", "GB82WEST12345698765432")]
    [InlineData("Acct BE68 5390 0754 7034 ABCD", "BE68 5390 0754 7034", "BE68539007547034")]
    public void Iban_is_validated_and_compacted(string text, string value, string normalized)
    {
        var match = Assert.Single(Find(text, OcrPatterns.Iban));
        Assert.Equal(value, match.Value);
        Assert.Equal(normalized, match.Normalized);
    }

    [Theory]
    [InlineData("FRl4 2004 1010 0505 0001 3M02 606", "FR1420041010050500013M02606")]
    [InlineData("DE89 37O4 0044 0532 0130 00", "DE89370400440532013000")]
    public void Iban_ocr_lookalikes_are_repaired_for_validation_but_value_is_unchanged(string text, string normalized)
    {
        var match = Assert.Single(Find(text, OcrPatterns.Iban));
        Assert.Equal(text, match.Value);
        Assert.Equal(normalized, match.Normalized);
    }

    [Fact]
    public void Iban_lookalike_repair_never_invents_a_valid_number()
    {
        // "8O" repairs to "80", which fails mod-97 (the real check digits are 89): nothing is reported.
        Assert.Empty(Find("DE8O 3704 0044 0532 0130 00", OcrPatterns.Iban));
    }

    [Fact]
    public void Iban_with_bad_checksum_is_not_reported_as_the_full_number()
    {
        var matches = Find("DE89 3704 0044 0532 0130 01", OcrPatterns.Iban);
        Assert.DoesNotContain(matches, m => m.Normalized == "DE89370400440532013001");
    }

    [Theory]
    [InlineData("Card 4111 1111 1111 1111 exp 12/26", "4111 1111 1111 1111", "4111111111111111")]
    [InlineData("5555-5555-5555-4444", "5555-5555-5555-4444", "5555555555554444")]
    [InlineData("4111111111111111", "4111111111111111", "4111111111111111")]
    [InlineData("4111 1111 1111 111l", "4111 1111 1111 111l", "4111111111111111")]
    public void Payment_card_is_luhn_validated(string text, string value, string digits)
    {
        var match = Assert.Single(Find(text, OcrPatterns.PaymentCard));
        Assert.Equal(value, match.Value);
        Assert.Equal(digits, match.Normalized);
    }

    [Theory]
    [InlineData("4111 1111 1111 1112")]
    [InlineData("1111 1111 1111 1111")]
    [InlineData("0000 0000 0000 0000")]
    public void Payment_card_rejects_invalid_numbers(string text)
    {
        Assert.Empty(Find(text, OcrPatterns.PaymentCard));
    }

    [Fact]
    public void Percentage_normalizes_sign_and_decimal()
    {
        var matches = Find("VAT 20% and -2,5 % off", OcrPatterns.Percentage);
        Assert.Equal(new[] { "20%", "-2.5%" }, matches.Select(m => m.Normalized));
    }

    [Fact]
    public void All_patterns_merge_in_page_order()
    {
        var result = Result(
            Line("Invoice date 12 Mar 2026"),
            Line("Total €1.234,56 VAT 20%"),
            Line("Mail billing@acme.test"));

        var matches = result.FindMatches(OcrPatterns.All);

        Assert.Equal(
            new[] { OcrMatchKind.Date, OcrMatchKind.Amount, OcrMatchKind.Percentage, OcrMatchKind.Email },
            matches.Select(m => m.Kind));
        Assert.Equal(new[] { 0, 1, 1, 2 }, matches.Select(m => m.LineIndex));
    }
}
