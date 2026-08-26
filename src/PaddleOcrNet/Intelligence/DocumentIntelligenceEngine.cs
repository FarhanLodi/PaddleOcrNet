using System.Text;
using System.Text.Json;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using PaddleOcrNet.Structure;
using EasyImageSharp;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

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

    /// <summary>
    /// Built-in system prompt for key-information extraction.
    /// </summary>
    private const string DefaultExtractionSystemPrompt =
        "You are a precise document field extractor. You are given a document rendered as Markdown and a " +
        "list of field names to extract. Return ONLY a single JSON object that maps each requested field " +
        "name (verbatim, exactly as given) to the string value found in the document, or to JSON null when " +
        "the field is not present in the document. Do not invent or infer values that are not supported by " +
        "the document. Do not include any field that was not requested. Do not output any prose, " +
        "explanation, or Markdown code fences — output the raw JSON object only.";

    /// <summary>
    /// Built-in system prompt for document question-answering.
    /// </summary>
    private const string DefaultQaSystemPrompt =
        "You are a careful document question-answering assistant. Answer the user's question using ONLY the " +
        "information contained in the supplied document. Do not use outside knowledge and do not guess. If " +
        "the document does not contain the answer, say that you cannot find the answer in the document.";

    /// <summary>
    /// Built-in system prompt for chart-to-data parsing (sent with a single cropped chart image).
    /// </summary>
    private const string DefaultChartSystemPrompt =
        "You are a precise chart-data extractor. You are given an image of a single chart (bar, line, pie, " +
        "scatter, area, etc.). Return ONLY a single JSON object with exactly these fields: \"chart_type\" " +
        "(string), \"title\" (string or null), \"data_markdown\" (a GitHub-flavored Markdown table that " +
        "reconstructs the chart's underlying data — category/axis labels as columns, one row per data point " +
        "or series), and \"description\" (a one-sentence summary). Use only values visible in the chart; do " +
        "not invent data. Output the raw JSON object only — no prose, no Markdown code fences.";

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

    /// <summary>
    /// Builds the KIE user message: the document Markdown plus the explicit list of keys to extract.
    /// </summary>
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

    /// <summary>
    /// Renders a JSON value to its string form, or <c>null</c> for JSON null / undefined.
    /// </summary>
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
    // Chart-to-data parsing
    // ----------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ChartParseResult> ParseChartsAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);

        using var image = await Image.LoadAsync<Rgb24>(Path.GetFullPath(imagePath), cancellationToken).ConfigureAwait(false);
        var document = await _ocr.AnalyzeDocumentAsync(image, _options.StructureOptions, cancellationToken).ConfigureAwait(false);
        return await ParseChartsCoreAsync(document, image, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ChartParseResult> ParseChartsAsync(Image<Rgb24> image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Caller retains ownership of the image — do NOT dispose it here.
        var document = await _ocr.AnalyzeDocumentAsync(image, _options.StructureOptions, cancellationToken).ConfigureAwait(false);
        return await ParseChartsCoreAsync(document, image, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ChartParseResult> ParseChartsAsync(StructureResult document, Image<Rgb24> image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(image);

        return ParseChartsCoreAsync(document, image, cancellationToken);
    }

    /// <summary>
    /// Parses every <see cref="StructureBlockType.Chart"/> block in <paramref name="document"/>: crops the
    /// region from <paramref name="image"/>, PNG-encodes it, and asks the vision-capable
    /// <see cref="IChatModel"/> to reconstruct the chart's data. Degenerate crops (clamped width/height
    /// &lt; 2 px) are skipped. Returns <see cref="ChartParseResult.Empty"/> when the document has no charts.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The document contains a chart region but the configured <see cref="IChatModel"/> does not support vision.
    /// </exception>
    private async Task<ChartParseResult> ParseChartsCoreAsync(StructureResult document, Image<Rgb24> image, CancellationToken cancellationToken)
    {
        var chartBlocks = document.Blocks
            .Where(b => b.Type == StructureBlockType.Chart)
            .OrderBy(b => b.Order)
            .ToList();

        if (chartBlocks.Count == 0)
            return ChartParseResult.Empty;

        if (!_chatModel.SupportsVision)
            throw new NotSupportedException(
                "Chart parsing requires a vision-capable IChatModel (configure a multimodal model such as " +
                "gpt-4o or qwen2.5-vl / llama3.2-vision via Ollama). The configured chat model reports " +
                "SupportsVision = false.");

        string systemPrompt = _options.ChartExtractionSystemPromptOverride ?? DefaultChartSystemPrompt;
        var parsed = new List<ParsedChart>(chartBlocks.Count);

        foreach (var block in chartBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[]? pngBytes = CropChartToPng(image, block.Bounds);
            if (pngBytes is null)
                continue; // Degenerate crop — skip this block.

            var request = new ChatRequest
            {
                Messages = new[]
                {
                    ChatMessage.System(systemPrompt),
                    ChatMessage.User(
                        "Extract the underlying data from this chart as specified.",
                        new[] { new ChatImage(pngBytes, "image/png") }),
                },
                Temperature = _options.Temperature,
                MaxTokens = _options.MaxTokens,
                JsonMode = true,
            };

            var response = await _chatModel.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

            string? rawJson = ExtractJsonObject(response.Text);
            ParseChartFields(rawJson, out string? chartType, out string? title, out string? dataMarkdown, out string? description);

            parsed.Add(new ParsedChart
            {
                Order = block.Order,
                Bounds = block.Bounds,
                ChartType = chartType,
                Title = title,
                DataMarkdown = dataMarkdown ?? string.Empty,
                Description = description,
                RawJson = rawJson,
                Usage = response.Usage,
                Model = response.Model,
            });
        }

        ChatUsage? aggregate = AggregateUsage(parsed);
        string? model = parsed.Select(p => p.Model).FirstOrDefault(m => m is not null);

        return new ChartParseResult
        {
            Charts = parsed,
            Usage = aggregate,
            Model = model,
        };
    }

    /// <summary>
    /// Reads the optional <c>chart_type</c>, <c>title</c>, <c>data_markdown</c> and <c>description</c> string
    /// fields out of the model's JSON reply. Missing properties, JSON <c>null</c>, non-string values, or an
    /// unparseable reply all yield <c>null</c> for that field.
    /// </summary>
    private static void ParseChartFields(
        string? rawJson,
        out string? chartType,
        out string? title,
        out string? dataMarkdown,
        out string? description)
    {
        chartType = title = dataMarkdown = description = null;

        if (string.IsNullOrWhiteSpace(rawJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            var root = doc.RootElement;
            chartType = ReadStringField(root, "chart_type");
            title = ReadStringField(root, "title");
            dataMarkdown = ReadStringField(root, "data_markdown");
            description = ReadStringField(root, "description");
        }
        catch (JsonException)
        {
            // Malformed reply — leave every field null.
        }
    }

    /// <summary>
    /// Returns the property's value only when it exists and is a JSON string; otherwise <c>null</c>.
    /// </summary>
    private static string? ReadStringField(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>
    /// Sums <see cref="ChatUsage.PromptTokens"/> and <see cref="ChatUsage.CompletionTokens"/> across every
    /// parsed chart whose usage the provider reported; returns <c>null</c> when no chart reported usage.
    /// </summary>
    private static ChatUsage? AggregateUsage(IReadOnlyList<ParsedChart> charts)
    {
        int prompt = 0;
        int completion = 0;
        bool any = false;

        foreach (var chart in charts)
        {
            if (chart.Usage is { } usage)
            {
                prompt += usage.PromptTokens;
                completion += usage.CompletionTokens;
                any = true;
            }
        }

        return any ? new ChatUsage(prompt, completion) : null;
    }

    /// <summary>
    /// Crops the chart region described by <paramref name="bounds"/> out of <paramref name="image"/> and
    /// PNG-encodes it. Bounds are clamped to the image and the crop is skipped (returns <c>null</c>) when the
    /// clamped region is degenerate (width or height &lt; 2 px).
    /// </summary>
    private static byte[]? CropChartToPng(Image<Rgb24> image, OcrBoundingBox bounds)
    {
        int x = Math.Clamp((int)Math.Floor(bounds.MinX), 0, image.Width - 1);
        int y = Math.Clamp((int)Math.Floor(bounds.MinY), 0, image.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(bounds.MaxX), 0, image.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(bounds.MaxY), 0, image.Height);
        int w = right - x;
        int h = bottom - y;

        if (w < 2 || h < 2)
            return null;

        using var crop = image.Clone(ctx => ctx.Crop(new Rectangle(x, y, w, h)));
        using var stream = new MemoryStream();
        crop.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    // ----------------------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Validates the requested key list (non-null, non-empty, every key non-blank).
    /// </summary>
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

    /// <summary>
    /// Removes leading/trailing Markdown code fences (<c>```json</c> … <c>```</c>) if present.
    /// </summary>
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
