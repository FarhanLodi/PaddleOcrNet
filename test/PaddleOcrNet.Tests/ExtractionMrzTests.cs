using PaddleOcrNet.Extraction;
using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-free tests for <see cref="MrzParser"/> using the ICAO Doc 9303 specimen MRZs, OCR misreads the
/// positional corrections repair, and a broken check digit.
/// </summary>
public class ExtractionMrzTests
{
    private static readonly string Td3Line1 = "P<UTOERIKSSON<<ANNA<MARIA".PadRight(44, '<');
    private const string Td3Line2 = "L898902C36UTO7408122F1204159ZE184226B<<<<<10";

    private const string Td1Line1 = "I<UTOD231458907<<<<<<<<<<<<<<<";
    private const string Td1Line2 = "7408122F1204159UTO<<<<<<<<<<<6";
    private const string Td1Line3 = "ERIKSSON<<ANNA<MARIA<<<<<<<<<<";

    private static readonly string Td2Line1 = "I<UTOERIKSSON<<ANNA<MARIA".PadRight(36, '<');
    private const string Td2Line2 = "D231458907UTO7408122F1204159<<<<<<<6";

    private static void AssertSpecimenHolder(MrzResult mrz)
    {
        Assert.Equal("UTO", mrz.IssuingCountry);
        Assert.Equal("ERIKSSON", mrz.Surname);
        Assert.Equal("ANNA MARIA", mrz.GivenNames);
        Assert.Equal("UTO", mrz.Nationality);
        Assert.Equal(new DateOnly(1974, 8, 12), mrz.BirthDate);
        Assert.Equal("F", mrz.Sex);
        Assert.Equal(new DateOnly(2012, 4, 15), mrz.ExpiryDate);
    }

    [Fact]
    public void Td3_specimen_parses_and_validates()
    {
        Assert.True(MrzParser.TryParse(new[] { Td3Line1, Td3Line2 }, out var mrz));

        Assert.Equal(MrzFormat.Td3, mrz.Format);
        Assert.Equal("P", mrz.DocumentType);
        Assert.Equal("L898902C3", mrz.DocumentNumber);
        Assert.Equal("ZE184226B", mrz.OptionalData);
        Assert.Null(mrz.OptionalData2);
        AssertSpecimenHolder(mrz);
        Assert.Equal(new MrzCheckResults(true, true, true, true, true), mrz.Checks);
        Assert.True(mrz.IsValid);
        Assert.Equal(new[] { Td3Line1, Td3Line2 }, mrz.RawLines);
    }

    [Fact]
    public void Td2_specimen_parses_and_validates()
    {
        Assert.True(MrzParser.TryParse(new[] { Td2Line1, Td2Line2 }, out var mrz));

        Assert.Equal(MrzFormat.Td2, mrz.Format);
        Assert.Equal("I", mrz.DocumentType);
        Assert.Equal("D23145890", mrz.DocumentNumber);
        AssertSpecimenHolder(mrz);
        Assert.Null(mrz.Checks.OptionalData);
        Assert.True(mrz.IsValid);
    }

    [Fact]
    public void Td1_specimen_parses_and_validates()
    {
        Assert.True(MrzParser.TryParse(new[] { Td1Line1, Td1Line2, Td1Line3 }, out var mrz));

        Assert.Equal(MrzFormat.Td1, mrz.Format);
        Assert.Equal("I", mrz.DocumentType);
        Assert.Equal("D23145890", mrz.DocumentNumber);
        Assert.Equal(string.Empty, mrz.OptionalData);
        Assert.Equal(string.Empty, mrz.OptionalData2);
        AssertSpecimenHolder(mrz);
        Assert.True(mrz.IsValid);
    }

    [Fact]
    public void Misread_td3_is_repaired_by_normalization_and_positional_corrections()
    {
        // Letter/digit swaps in both directions, inserted spaces, and trailing fillers read as K.
        string line1 = "P<UT0 ERIKSS0N<<ANNA<MARIA" + new string('K', 19);
        string line2 = "L898902C3 6UT0 74O8122F12O4159ZE184226B<<<<<1O";

        Assert.True(MrzParser.TryParse(new[] { "REPUBLIC OF UTOPIA", line1, line2 }, out var mrz));

        Assert.True(mrz.IsValid);
        Assert.Equal("L898902C3", mrz.DocumentNumber);
        AssertSpecimenHolder(mrz);
        Assert.StartsWith("P<UT0ERIKSS0N", mrz.RawLines[0]);
        Assert.EndsWith("<<<<", mrz.RawLines[0]);
    }

    [Fact]
    public void Guillemet_is_read_as_a_double_filler()
    {
        string line1 = "P<UTOERIKSSON«ANNA<MARIA" + new string('<', 19);
        Assert.True(MrzParser.TryParse(new[] { line1, Td3Line2 }, out var mrz));
        Assert.True(mrz.IsValid);
        Assert.Equal("ANNA MARIA", mrz.GivenNames);
    }

    [Fact]
    public void Short_filler_run_is_padded()
    {
        Assert.True(MrzParser.TryParse(new[] { Td3Line1[..41], Td3Line2 }, out var mrz));
        Assert.True(mrz.IsValid);
    }

    [Fact]
    public void Wrong_check_digit_parses_but_is_invalid()
    {
        string badExpiryCheck = Td3Line2[..27] + "8" + Td3Line2[28..];

        Assert.True(MrzParser.TryParse(new[] { Td3Line1, badExpiryCheck }, out var mrz));

        Assert.False(mrz.IsValid);
        Assert.False(mrz.Checks.ExpiryDate);
        Assert.True(mrz.Checks.DocumentNumber);
        Assert.True(mrz.Checks.BirthDate);
    }

    [Fact]
    public void TryParse_result_joins_split_detections_on_the_same_row()
    {
        var result = new OcrResult
        {
            FullText = string.Empty,
            Languages = new[] { "en" },
            Lines = new[]
            {
                new OcrLine { Text = "PASSPORT", BoundingBox = new OcrBoundingBox(0, -60, 120, -40) },
                new OcrLine { Text = Td3Line2, BoundingBox = new OcrBoundingBox(0, 30, 440, 50) },
                new OcrLine { Text = Td3Line1[..15], BoundingBox = new OcrBoundingBox(0, 0, 150, 20) },
                new OcrLine { Text = Td3Line1[15..], BoundingBox = new OcrBoundingBox(160, 1, 440, 21) },
            },
        };

        Assert.True(MrzParser.TryParse(result, out var mrz));
        Assert.True(mrz.IsValid);
        Assert.Equal("ERIKSSON", mrz.Surname);
    }

    [Fact]
    public void Non_mrz_text_is_not_parsed()
    {
        Assert.False(MrzParser.TryParse(new[] { "Hello world", "Nothing to see here" }, out var mrz));
        Assert.Null(mrz);
    }

    [Theory]
    [InlineData("D23145890", 7)]
    [InlineData("740812", 2)]
    [InlineData("120415", 9)]
    [InlineData("<<<<<<", 0)]
    [InlineData("abc", -1)]
    public void Check_digit_uses_icao_weights(string data, int expected)
    {
        Assert.Equal(expected, MrzParser.ComputeCheckDigit(data));
    }

    [Fact]
    public void Recommended_options_restrict_to_the_mrz_alphabet()
    {
        var options = MrzParser.RecommendedRecognitionOptions;
        Assert.NotNull(options.Allowlist);
        Assert.Equal(37, options.Allowlist!.Count);
        Assert.Contains("<", options.Allowlist);
        Assert.Contains("Z", options.Allowlist);
        Assert.Contains("0", options.Allowlist);
        Assert.DoesNotContain("a", options.Allowlist);
    }
}
