using PaddleOcrNet.Intelligence;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using PaddleOcrNet.Structure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no network, no model download, CI-safe) for <see cref="DocumentIntelligenceEngine"/>.
/// A <see cref="FakeChatModel"/> returns canned JSON / answers and records the <see cref="ChatRequest"/> it
/// received, so the tests assert the engine's prompt strategy (JSON mode set, keys present), its robust JSON
/// parsing (object mapped to fields, missing key -> null, code-fence tolerance), and the Q&amp;A return value.
/// The <see cref="StructureResult"/> overloads are used so the fake OCR service is never invoked.
/// </summary>
public class DocumentIntelligenceTests
{
    // -----------------------------------------------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------------------------------------------

    /// <summary>An <see cref="IChatModel"/> that returns a canned reply and records the last request.</summary>
    private sealed class FakeChatModel : IChatModel
    {
        private readonly string _reply;

        public FakeChatModel(string reply, bool supportsVision = false)
        {
            _reply = reply;
            SupportsVision = supportsVision;
        }

        public ChatRequest? LastRequest { get; private set; }

        public bool SupportsVision { get; }

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var response = new ChatResponse
            {
                Text = _reply,
                Usage = new ChatUsage(11, 7),
                Model = "fake-model",
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// A minimal <see cref="IPaddleOcrService"/> for the engine. The <see cref="StructureResult"/> overloads
    /// bypass it; the image/path overloads route through <see cref="AnalyzeDocumentAsync(Image{Rgb24}, StructureOptions, CancellationToken)"/>,
    /// which returns the canned <see cref="Document"/> so the vision-attachment tests can exercise that path.
    /// </summary>
    private sealed class FakeOcrService : IPaddleOcrService
    {
        public Task<StructureResult> AnalyzeDocumentAsync(Image<Rgb24> image, StructureOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Document());

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

    // -----------------------------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------------------------

    private static OcrBoundingBox Box(double x1, double y1, double x2, double y2) => new(x1, y1, x2, y2);

    private static StructureResult Document() => new()
    {
        Blocks = new[]
        {
            new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 600, 40), Order: 0, Text: "Invoice"),
            new StructureBlock(StructureBlockType.Text, Box(0, 50, 600, 120), Order: 1,
                Text: "Invoice Number: INV-42\nVendor: Acme Corp\nTotal: $99.50"),
        },
        SourceWidth = 600,
        SourceHeight = 800,
    };

    private static DocumentIntelligenceEngine Engine(FakeChatModel chat, DocumentIntelligenceOptions? options = null)
        => new(new FakeOcrService(), chat, options);

    // -----------------------------------------------------------------------------------------------------
    // Constructor validation
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Ctor_rejects_null_dependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new DocumentIntelligenceEngine(null!, new FakeChatModel("{}")));
        Assert.Throws<ArgumentNullException>(() => new DocumentIntelligenceEngine(new FakeOcrService(), null!));
    }

    // -----------------------------------------------------------------------------------------------------
    // Key-information extraction
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Extract_parses_json_object_into_fields()
    {
        var chat = new FakeChatModel("{\"Invoice Number\": \"INV-42\", \"Vendor\": \"Acme Corp\"}");
        var keys = new[] { "Invoice Number", "Vendor" };

        var result = await Engine(chat).ExtractKeyInformationAsync(Document(), keys);

        Assert.Equal("INV-42", result["Invoice Number"]);
        Assert.Equal("Acme Corp", result["Vendor"]);
        Assert.Equal(2, result.Fields.Count);
    }

    [Fact]
    public async Task Extract_missing_key_yields_null_value_but_is_still_present()
    {
        // The model omits "Vendor" entirely and returns explicit null for "Due Date".
        var chat = new FakeChatModel("{\"Invoice Number\": \"INV-42\", \"Due Date\": null}");
        var keys = new[] { "Invoice Number", "Vendor", "Due Date" };

        var result = await Engine(chat).ExtractKeyInformationAsync(Document(), keys);

        // Every requested key produces a field, in request order.
        Assert.Equal(keys, result.Fields.Select(f => f.Key).ToArray());

        Assert.Equal("INV-42", result["Invoice Number"]);

        // Omitted key -> present field with null value.
        Assert.True(result.TryGet("Vendor", out var vendor));
        Assert.Null(vendor);

        // Explicit JSON null -> present field with null value.
        Assert.True(result.TryGet("Due Date", out var due));
        Assert.Null(due);

        // A key that was never requested -> not present.
        Assert.False(result.TryGet("Total", out var total));
        Assert.Null(total);
    }

    [Fact]
    public async Task Extract_tolerates_json_code_fences_and_surrounding_prose()
    {
        var fenced =
            "Here is the extracted data:\n" +
            "```json\n" +
            "{\n  \"Invoice Number\": \"INV-42\",\n  \"Total\": \"$99.50\"\n}\n" +
            "```\n" +
            "Let me know if you need more.";
        var chat = new FakeChatModel(fenced);
        var keys = new[] { "Invoice Number", "Total" };

        var result = await Engine(chat).ExtractKeyInformationAsync(Document(), keys);

        Assert.Equal("INV-42", result["Invoice Number"]);
        Assert.Equal("$99.50", result["Total"]);
        Assert.NotNull(result.RawJson);
        Assert.StartsWith("{", result.RawJson!.TrimStart());
    }

    [Fact]
    public async Task Extract_sets_json_mode_and_includes_keys_and_document_in_prompt()
    {
        var chat = new FakeChatModel("{\"Vendor\": \"Acme Corp\"}");
        var keys = new[] { "Vendor" };

        await Engine(chat).ExtractKeyInformationAsync(Document(), keys);

        var req = chat.LastRequest;
        Assert.NotNull(req);

        // JsonMode must be set for KIE.
        Assert.True(req!.JsonMode);

        // System + user messages, with a system extractor instruction first.
        Assert.Equal(2, req.Messages.Count);
        Assert.Equal(ChatRole.System, req.Messages[0].Role);
        Assert.Equal(ChatRole.User, req.Messages[1].Role);

        var userText = req.Messages[1].Text;
        Assert.Contains("Vendor", userText);          // requested key listed
        Assert.Contains("Invoice", userText);          // document markdown grounded in
        Assert.Contains("Acme Corp", userText);        // document body included

        // Temperature defaults to 0 (deterministic).
        Assert.Equal(0d, req.Temperature);

        // No image attached by default (text-grounded).
        Assert.Null(req.Messages[1].Images);
    }

    [Fact]
    public async Task Extract_propagates_usage_and_model()
    {
        var chat = new FakeChatModel("{\"Vendor\": \"Acme Corp\"}");

        var result = await Engine(chat).ExtractKeyInformationAsync(Document(), new[] { "Vendor" });

        Assert.Equal("fake-model", result.Model);
        Assert.NotNull(result.Usage);
        Assert.Equal(18, result.Usage!.TotalTokens);
    }

    [Fact]
    public async Task Extract_unparseable_reply_yields_all_null_fields()
    {
        var chat = new FakeChatModel("I could not produce JSON, sorry.");

        var result = await Engine(chat).ExtractKeyInformationAsync(Document(), new[] { "Vendor", "Total" });

        Assert.All(result.Fields, f => Assert.Null(f.Value));
    }

    [Fact]
    public async Task Extract_rejects_empty_key_list()
    {
        var chat = new FakeChatModel("{}");
        await Assert.ThrowsAsync<ArgumentException>(
            () => Engine(chat).ExtractKeyInformationAsync(Document(), Array.Empty<string>()));
    }

    // -----------------------------------------------------------------------------------------------------
    // Document question-answering
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ask_returns_answer_text_and_metadata()
    {
        var chat = new FakeChatModel("The vendor is Acme Corp.");

        var answer = await Engine(chat).AskAsync(Document(), "Who is the vendor?");

        Assert.Equal("The vendor is Acme Corp.", answer.Answer);
        Assert.Equal("fake-model", answer.Model);
        Assert.NotNull(answer.Usage);
    }

    [Fact]
    public async Task Ask_does_not_set_json_mode_and_grounds_on_document_and_question()
    {
        var chat = new FakeChatModel("Acme Corp");

        await Engine(chat).AskAsync(Document(), "Who is the vendor?");

        var req = chat.LastRequest;
        Assert.NotNull(req);
        Assert.False(req!.JsonMode);
        Assert.Equal(ChatRole.System, req.Messages[0].Role);

        var userText = req.Messages[1].Text;
        Assert.Contains("Who is the vendor?", userText);
        Assert.Contains("Acme Corp", userText);
    }

    [Fact]
    public async Task Ask_rejects_blank_question()
    {
        var chat = new FakeChatModel("x");
        await Assert.ThrowsAsync<ArgumentException>(() => Engine(chat).AskAsync(Document(), "   "));
    }

    // -----------------------------------------------------------------------------------------------------
    // Vision attachment
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Extract_attaches_png_image_when_vision_enabled_and_supported()
    {
        var chat = new FakeChatModel("{\"Vendor\": \"Acme Corp\"}", supportsVision: true);
        var options = new DocumentIntelligenceOptions { UseVision = true };
        using var image = new Image<Rgb24>(8, 8);

        await Engine(chat, options).ExtractKeyInformationAsync(image, new[] { "Vendor" });

        var images = chat.LastRequest!.Messages[1].Images;
        Assert.NotNull(images);
        var attached = Assert.Single(images!);
        Assert.Equal("image/png", attached.MediaType);
        Assert.True(attached.Data.Length > 0);
    }

    [Fact]
    public async Task Extract_omits_image_when_model_lacks_vision()
    {
        // UseVision requested, but the model does not support vision -> no attachment.
        var chat = new FakeChatModel("{\"Vendor\": \"Acme Corp\"}", supportsVision: false);
        var options = new DocumentIntelligenceOptions { UseVision = true };
        using var image = new Image<Rgb24>(8, 8);

        await Engine(chat, options).ExtractKeyInformationAsync(image, new[] { "Vendor" });

        Assert.Null(chat.LastRequest!.Messages[1].Images);
    }
}
