using System.Text;
using System.Text.Json;
using PaddleOcrNet.Services;
using PaddleOcrNet.Structure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace PaddleOcrNet.Intelligence;

/// <summary>
/// Default <see cref="IDocumentIntelligence"/> implementation. Analyzes a document's structure with an
/// injected <see cref="IPaddleOcrService"/>, serializes it to Markdown, and grounds an injected
/// <see cref="IChatModel"/> on that Markdown to perform Key-Information Extraction and document
/// question-answering. It depends only on the <see cref="IChatModel"/> seam, never on a concrete provider,
/// so any LLM backend can be plugged in via dependency injection.
/// </summary>
public sealed class DocumentIntelligenceEngine : IDocumentIntelligence
{
    private readonly IPaddleOcrService _ocr;
    private readonly IChatModel _chatModel;
    private readonly DocumentIntelligenceOptions _options;

    /// <summary>Built-in system prompt for key-information extraction.</summary>
    private const string DefaultExtractionSystemPrompt =
        "You are a precise document field extractor. You are given a document rendered as Markdown and a " +
        "list of field names to extract. Return ONLY a single JSON object that maps each requested field " +
        "name (verbatim, exactly as given) to the string value found in the document, or to JSON null when " +
        "the field is not present in the document. Do not invent or infer values that are not supported by " +
        "the document. Do not include any field that was not requested. Do not output any prose, " +
        "explanation, or Markdown code fences — output the raw JSON object only.";

    /// <summary>Built-in system prompt for document question-answering.</summary>
    private const string DefaultQaSystemPrompt =
        "You are a careful document question-answering assistant. Answer the user's question using ONLY the " +
        "information contained in the supplied document. Do not use outside knowledge and do not guess. If " +
        "the document does not contain the answer, say that you cannot find the answer in the document.";

    /// <summary>
    /// Creates the engine.
    /// </summary>
    /// <param name="ocr">The OCR / structure-analysis service used to analyze document images.</param>
    /// <param name="chatModel">The provider-agnostic LLM client used for extraction and answering.</param>
    /// <param name="options">Engine options; <c>null</c> uses <see cref="DocumentIntelligenceOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ocr"/> or <paramref name="chatModel"/> is <c>null</c>.</exception>
    public DocumentIntelligenceEngine(IPaddleOcrService ocr, IChatModel chatModel, DocumentIntelligenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(chatModel);

        _ocr = ocr;
        _chatModel = chatModel;
        _options = options ?? DocumentIntelligenceOptions.Default;
    }

    // ----------------------------------------------------------------------------------------------------
    // Key-information extraction
    // ----------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<KeyInformationResult> ExtractKeyInformationAsync(string imagePath, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        ValidateKeys(keys);

        var document = await _ocr.AnalyzeDocumentAsync(imagePath, _options.StructureOptions, cancellationToken).ConfigureAwait(false);
        return await ExtractKeyInformationAsync(document, keys, image: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<KeyInformationResult> ExtractKeyInformationAsync(Image<Rgb24> image, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateKeys(keys);

        var document = await _ocr.AnalyzeDocumentAsync(image, _options.StructureOptions, cancellationToken).ConfigureAwait(false);
        return await ExtractKeyInformationAsync(document, keys, image, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<KeyInformationResult> ExtractKeyInformationAsync(StructureResult document, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateKeys(keys);

        return ExtractKeyInformationAsync(document, keys, image: null, cancellationToken);
    }

    private async Task<KeyInformationResult> ExtractKeyInformationAsync(
        StructureResult document,
        IReadOnlyList<string> keys,
        Image<Rgb24>? image,
        CancellationToken cancellationToken)
    {
        string markdown = document.ToMarkdown();
        string systemPrompt = _options.SystemPromptOverride ?? DefaultExtractionSystemPrompt;
        string userPrompt = BuildExtractionUserPrompt(markdown, keys);

        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.System(systemPrompt),
                ChatMessage.User(userPrompt, BuildImages(image)),
            },
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
            JsonMode = true,
        };

        var response = await _chatModel.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        string? rawJson = ExtractJsonObject(response.Text);
        var fields = ParseFields(rawJson, keys);

        return new KeyInformationResult
        {
            Fields = fields,
            RawJson = rawJson,
            Usage = response.Usage,
            Model = response.Model,
        };
    }

    /// <summary>Builds the KIE user message: the document Markdown plus the explicit list of keys to extract.</summary>
    private static string BuildExtractionUserPrompt(string markdown, IReadOnlyList<string> keys)
    {
        var sb = new StringBuilder();
        sb.Append("Document (Markdown):\n\n");
        sb.Append(markdown);
        sb.Append("\n\nExtract the following fields and return ONLY a JSON object keyed by these exact names:\n");
        foreach (var key in keys)
        {
            sb.Append("- ");
            sb.Append(key);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses the model's JSON object into one <see cref="ExtractedField"/> per requested key. Missing keys,
    /// JSON <c>null</c>, or an unparseable reply all yield a <c>null</c> value for that key. Non-string JSON
    /// values (numbers/booleans) are rendered to their text form; objects/arrays are serialized compactly.
    /// </summary>
    private static IReadOnlyList<ExtractedField> ParseFields(string? rawJson, IReadOnlyList<string> keys)
    {
        var fields = new List<ExtractedField>(keys.Count);

        JsonElement root = default;
        bool haveObject = false;
        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // Clone so the element stays valid after the JsonDocument is disposed.
                    root = doc.RootElement.Clone();
                    haveObject = true;
                }
            }
            catch (JsonException)
            {
                // Malformed reply — every requested field resolves to null below.
            }
        }

        foreach (var key in keys)
        {
            string? value = null;
            if (haveObject && root.TryGetProperty(key, out var element))
                value = ValueToString(element);

            fields.Add(new ExtractedField(key, value));
        }

        return fields;
    }

    /// <summary>Renders a JSON value to its string form, or <c>null</c> for JSON null / undefined.</summary>
    private static string? ValueToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        // Objects / arrays: keep their compact JSON so the caller still gets the value.
        _ => element.GetRawText(),
    };

    // ----------------------------------------------------------------------------------------------------
    // Document question-answering
    // ----------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<DocumentAnswer> AskAsync(string imagePath, string question, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var document = await _ocr.AnalyzeDocumentAsync(imagePath, _options.StructureOptions, cancellationToken).ConfigureAwait(false);
        return await AskAsync(document, question, image: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<DocumentAnswer> AskAsync(StructureResult document, string question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        return AskAsync(document, question, image: null, cancellationToken);
    }

    private async Task<DocumentAnswer> AskAsync(
        StructureResult document,
        string question,
        Image<Rgb24>? image,
        CancellationToken cancellationToken)
    {
        string markdown = document.ToMarkdown();
        string systemPrompt = _options.SystemPromptOverride ?? DefaultQaSystemPrompt;

        var userPrompt = new StringBuilder()
            .Append("Document (Markdown):\n\n")
            .Append(markdown)
            .Append("\n\nQuestion: ")
            .Append(question)
            .ToString();

        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.System(systemPrompt),
                ChatMessage.User(userPrompt, BuildImages(image)),
            },
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
        };

        var response = await _chatModel.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        return new DocumentAnswer
        {
            Answer = response.Text,
            Usage = response.Usage,
            Model = response.Model,
        };
    }

    // ----------------------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Validates the requested key list (non-null, non-empty, every key non-blank).</summary>
    private static void ValidateKeys(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            throw new ArgumentException("At least one key must be requested.", nameof(keys));

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Requested keys must not be null or blank.", nameof(keys));
        }
    }

    /// <summary>
    /// Builds the optional image attachment list. Returns the PNG-encoded page image only when vision is
    /// requested, the model supports it, and an image is actually available; otherwise <c>null</c> (text-only).
    /// </summary>
    private IReadOnlyList<ChatImage>? BuildImages(Image<Rgb24>? image)
    {
        if (!_options.UseVision || !_chatModel.SupportsVision || image is null)
            return null;

        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return new[] { new ChatImage(stream.ToArray(), "image/png") };
    }

    /// <summary>
    /// Robustly locates a single JSON object inside a model reply. Tolerates models that wrap the object in
    /// Markdown code fences (<c>```json … ```</c>) or surround it with prose: strips fences, then returns the
    /// substring from the first <c>{</c> to its matching <c>}</c> (tracking brace depth and skipping braces
    /// inside JSON strings). Returns <c>null</c> when no plausible object is found.
    /// </summary>
    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string s = StripCodeFences(text).Trim();

        int start = s.IndexOf('{');
        if (start < 0)
            return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];

            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return s.Substring(start, i - start + 1);
                    break;
            }
        }

        // Unbalanced — return from the first brace to the end as a best effort; parsing will reject it if bad.
        return s.Substring(start);
    }

    /// <summary>Removes leading/trailing Markdown code fences (<c>```json</c> … <c>```</c>) if present.</summary>
    private static string StripCodeFences(string text)
    {
        string t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
            return t;

        // Drop the opening fence line (``` or ```json etc.).
        int firstNewline = t.IndexOf('\n');
        if (firstNewline >= 0)
            t = t[(firstNewline + 1)..];
        else
            t = t[3..];

        // Drop a trailing closing fence.
        int lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0)
            t = t[..lastFence];

        return t.Trim();
    }
}
