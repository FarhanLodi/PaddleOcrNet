using PaddleOcrNet.Intelligence.Offline;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-geometry tests (no network, no model, CI-safe) for <see cref="OfflineKeyInformationExtractor"/>. Each
/// test hand-builds an <see cref="OcrResult"/> from <see cref="OcrLine"/>s with explicit bounding boxes so the
/// heuristic precedence (inline same-line, value-to-the-right, value-below) resolves deterministically. The
/// core tests call <see cref="OfflineKeyInformationExtractor.Extract"/>, which never touches the OCR service, so
/// a throwing <see cref="ThrowingOcrService"/> stub stands in for the constructor dependency.
/// </summary>
public class OfflineKieTests
{
    // -----------------------------------------------------------------------------------------------------
    // Test doubles + fixtures
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// An <see cref="IPaddleOcrService"/> whose every member throws — the synchronous <c>Extract</c> path never
    /// calls it, so this proves the geometry tests are model-free.
    /// </summary>
    private sealed class ThrowingOcrService : IPaddleOcrService
    {
        public Task<OcrResult> ExtractTextFromImage(string imagePath, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(Stream imageStream, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(byte[] imageBytes, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(ReadOnlyMemory<byte> imageBytes, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(Image<Rgb24> image, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static OfflineKeyInformationExtractor Extractor() => new(new ThrowingOcrService());

    private static OcrLine Line(string text, double x1, double y1, double x2, double y2) => new()
    {
        Text = text,
        Confidence = 1.0,
        BoundingBox = new OcrBoundingBox(x1, y1, x2, y2),
    };

    private static OcrResult Result(params OcrLine[] lines) => new()
    {
        FullText = string.Join('\n', lines.Select(l => l.Text)),
        Lines = lines,
        Languages = Array.Empty<string>(),
    };

    // -----------------------------------------------------------------------------------------------------
    // Constructor validation
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Ctor_rejects_null_service()
    {
        Assert.Throws<ArgumentNullException>(() => new OfflineKeyInformationExtractor(null!));
    }

    [Fact]
    public void Extract_rejects_empty_key_list()
    {
        var ocr = Result(Line("Anything", 0, 0, 100, 20));
        Assert.Throws<ArgumentException>(() => Extractor().Extract(ocr, Array.Empty<string>()));
    }

    [Fact]
    public void Extract_rejects_blank_key()
    {
        var ocr = Result(Line("Anything", 0, 0, 100, 20));
        Assert.Throws<ArgumentException>(() => Extractor().Extract(ocr, new[] { "   " }));
    }

    // -----------------------------------------------------------------------------------------------------
    // Strategy 1 — inline same-line
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Extract_inline_colon_separated_value()
    {
        var ocr = Result(Line("Invoice Number: INV-42", 0, 0, 300, 20));

        var result = Extractor().Extract(ocr, new[] { "Invoice Number" });

        Assert.Equal("INV-42", result["Invoice Number"]);
    }

    [Fact]
    public void Extract_inline_matches_key_with_trailing_colon_and_ignores_case()
    {
        var ocr = Result(Line("Total: $99.50", 0, 0, 200, 20));

        // Key supplied with a trailing colon and different casing still resolves.
        var result = Extractor().Extract(ocr, new[] { "total:" });

        Assert.Equal("$99.50", result["total:"]);
    }

    // -----------------------------------------------------------------------------------------------------
    // Strategy 2 — label-left / value-right on the same row
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Extract_value_to_the_right_on_same_row()
    {
        // "Vendor" is a standalone label cell; the value sits to its right with overlapping Y.
        var ocr = Result(
            Line("Vendor", 10, 100, 90, 124),
            Line("Acme Corp", 140, 102, 320, 122));

        var result = Extractor().Extract(ocr, new[] { "Vendor" });

        Assert.Equal("Acme Corp", result["Vendor"]);
    }

    [Fact]
    public void Extract_right_picks_nearest_value_by_x_gap()
    {
        // Two candidates to the right on the same row; the nearer (smaller X gap) wins.
        var ocr = Result(
            Line("Name", 10, 100, 80, 124),
            Line("John Smith", 100, 102, 220, 122),
            Line("ignored-far", 400, 102, 520, 122));

        var result = Extractor().Extract(ocr, new[] { "Name" });

        Assert.Equal("John Smith", result["Name"]);
    }

    // -----------------------------------------------------------------------------------------------------
    // Strategy 3 — label-above / value-below
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Extract_value_below_label()
    {
        // "Date" label with the value in the cell directly beneath it (overlapping X).
        var ocr = Result(
            Line("Date", 10, 100, 90, 124),
            Line("2026-06-21", 12, 140, 110, 162));

        var result = Extractor().Extract(ocr, new[] { "Date" });

        Assert.Equal("2026-06-21", result["Date"]);
    }

    [Fact]
    public void Extract_inline_takes_precedence_over_right_and_below()
    {
        // An inline match and a (would-be) right/below match both exist; inline must win.
        var ocr = Result(
            Line("Amount: 100", 10, 100, 200, 124),
            Line("Amount", 10, 200, 90, 224),
            Line("999", 140, 202, 220, 222));

        var result = Extractor().Extract(ocr, new[] { "Amount" });

        Assert.Equal("100", result["Amount"]);
    }

    // -----------------------------------------------------------------------------------------------------
    // Missing keys / robustness / result API
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Extract_missing_key_yields_null_value_but_is_present()
    {
        var ocr = Result(Line("Invoice Number: INV-42", 0, 0, 300, 20));

        var keys = new[] { "Invoice Number", "Vendor" };
        var result = Extractor().Extract(ocr, keys);

        // Every requested key produces a field, in request order.
        Assert.Equal(keys, result.Fields.Select(f => f.Key).ToArray());

        Assert.Equal("INV-42", result["Invoice Number"]);

        // Missing key -> present field with null value.
        Assert.True(result.TryGet("Vendor", out var vendor));
        Assert.Null(vendor);

        // A key never requested -> not present.
        Assert.False(result.TryGet("Total", out var total));
        Assert.Null(total);
    }

    [Fact]
    public void Extract_is_robust_to_empty_lines()
    {
        var ocr = Result(); // no lines at all

        var result = Extractor().Extract(ocr, new[] { "Invoice Number", "Vendor" });

        Assert.All(result.Fields, f => Assert.Null(f.Value));
        Assert.Equal(2, result.Fields.Count);
    }

    [Fact]
    public void Extract_sets_llm_metadata_to_null()
    {
        var ocr = Result(Line("Total: $99.50", 0, 0, 200, 20));

        var result = Extractor().Extract(ocr, new[] { "Total" });

        Assert.Null(result.RawJson);
        Assert.Null(result.Usage);
        Assert.Null(result.Model);
    }
}
