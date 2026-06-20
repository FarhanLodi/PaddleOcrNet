using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PaddleOcrNet.Intelligence.Internal;

namespace PaddleOcrNet.Intelligence;

/// <summary>
/// Thrown when an OpenAI-compatible chat endpoint returns a non-success HTTP status or an otherwise
/// unusable response. Carries the HTTP <see cref="StatusCode"/> (when known) and a truncated copy of the
/// response body in the message for diagnostics.
/// </summary>
public sealed class ChatModelException : Exception
{
    /// <summary>The HTTP status code the provider returned, when the failure came from an HTTP response.</summary>
    public int? StatusCode { get; }

    /// <summary>Creates a <see cref="ChatModelException"/> with a message and optional HTTP status code.</summary>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="statusCode">The HTTP status code, when applicable.</param>
    public ChatModelException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>Creates a <see cref="ChatModelException"/> wrapping an inner exception.</summary>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="innerException">The underlying cause.</param>
    /// <param name="statusCode">The HTTP status code, when applicable.</param>
    public ChatModelException(string message, Exception innerException, int? statusCode = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// An <see cref="IChatModel"/> for any OpenAI-style <c>chat/completions</c> service — OpenAI, Azure OpenAI,
/// Ollama, vLLM, LM Studio, Groq, Together, DeepSeek, Mistral, and the like. Configure the endpoint, auth,
/// and default model through <see cref="OpenAiCompatibleOptions"/> (or one of its provider factories), then
/// call <see cref="CompleteAsync"/>. Requests and responses are (de)serialized through a source-generated
/// JSON context, so the adapter is trim-/Native-AOT-safe.
/// </summary>
public sealed class OpenAiCompatibleChatModel : IChatModel, IDisposable
{
    private const int ErrorBodyMaxLength = 2000;

    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleOptions _options;
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;

    /// <summary>
    /// Creates a model that uses the supplied <paramref name="httpClient"/>. The caller owns the client's
    /// lifetime; <see cref="OpenAiCompatibleOptions.Timeout"/> is ignored (configure it on the client).
    /// </summary>
    /// <param name="httpClient">The HTTP client to send requests with.</param>
    /// <param name="options">The endpoint, auth, and default-model configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> or <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> is missing a base URL or default model.</exception>
    public OpenAiCompatibleChatModel(HttpClient httpClient, OpenAiCompatibleOptions options)
        : this(httpClient, options, ownsClient: false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
    }

    /// <summary>
    /// Creates a model that owns an internal <see cref="HttpClient"/>, honoring
    /// <see cref="OpenAiCompatibleOptions.Timeout"/>. Dispose the model to dispose the client.
    /// </summary>
    /// <param name="options">The endpoint, auth, and default-model configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> is missing a base URL or default model.</exception>
    public OpenAiCompatibleChatModel(OpenAiCompatibleOptions options)
        : this(CreateOwnedClient(options), options, ownsClient: true)
    {
    }

    private OpenAiCompatibleChatModel(HttpClient httpClient, OpenAiCompatibleOptions options, bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
            throw new ArgumentException("BaseUrl must be set.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.DefaultModel))
            throw new ArgumentException("DefaultModel must be set.", nameof(options));

        _httpClient = httpClient;
        _options = options;
        _ownsClient = ownsClient;
        _endpoint = options.ResolveEndpoint();
    }

    /// <inheritdoc />
    public bool SupportsVision => _options.SupportsVision;

    /// <inheritdoc />
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = BuildRequestBody(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, OpenAiChatJsonContext.Default.ChatCompletionRequest),
        };
        ApplyHeaders(httpRequest);

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatModelException($"The chat request to '{_endpoint}' failed: {ex.Message}", ex);
        }

        using (httpResponse)
        {
            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await ReadTruncatedAsync(httpResponse, cancellationToken).ConfigureAwait(false);
                throw new ChatModelException(
                    $"The chat endpoint returned HTTP {(int)httpResponse.StatusCode} ({httpResponse.ReasonPhrase}). Body: {errorBody}",
                    (int)httpResponse.StatusCode);
            }

            ChatCompletionResponse? parsed;
            try
            {
                parsed = await httpResponse.Content
                    .ReadFromJsonAsync(OpenAiChatJsonContext.Default.ChatCompletionResponse, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new ChatModelException($"The chat response could not be parsed as JSON: {ex.Message}", ex);
            }

            return MapResponse(parsed);
        }
    }

    /// <summary>Releases the internal <see cref="HttpClient"/> when this model owns it.</summary>
    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }

    // -----------------------------------------------------------------------------------------------------
    // Request building
    // -----------------------------------------------------------------------------------------------------

    private ChatCompletionRequest BuildRequestBody(ChatRequest request)
    {
        var body = new ChatCompletionRequest
        {
            Model = string.IsNullOrWhiteSpace(request.Model) ? _options.DefaultModel : request.Model!,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            ResponseFormat = request.JsonMode ? new ResponseFormat { Type = "json_object" } : null,
        };

        foreach (var message in request.Messages)
            body.Messages.Add(MapMessage(message));

        return body;
    }

    private static ChatRequestMessage MapMessage(ChatMessage message)
    {
        var role = message.Role switch
        {
            ChatRole.System => "system",
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            _ => "user",
        };

        var content = new MessageContent();
        if (message.Images is { Count: > 0 })
        {
            // Multimodal: a text part (always present, even if empty) followed by one image_url part per image.
            var parts = new List<ContentPart>(message.Images.Count + 1)
            {
                new() { Type = "text", Text = message.Text },
            };

            foreach (var image in message.Images)
            {
                var base64 = Convert.ToBase64String(image.Data.Span);
                parts.Add(new ContentPart
                {
                    Type = "image_url",
                    ImageUrl = new ImageUrl { Url = $"data:{image.MediaType};base64,{base64}" },
                });
            }

            content.Parts = parts;
        }
        else
        {
            content.Text = message.Text;
        }

        return new ChatRequestMessage { Role = role, Content = content };
    }

    private void ApplyHeaders(HttpRequestMessage httpRequest)
    {
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            var headerValue = _options.ApiKeyPrefix + _options.ApiKey;
            // Use TryAddWithoutValidation so non-standard header names / prefixes (e.g. Azure's bare "api-key")
            // are accepted without HttpClient's strict Authorization parsing.
            httpRequest.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, headerValue);
        }

        if (_options.AdditionalHeaders is { Count: > 0 })
        {
            foreach (var header in _options.AdditionalHeaders)
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    // -----------------------------------------------------------------------------------------------------
    // Response mapping
    // -----------------------------------------------------------------------------------------------------

    private static ChatResponse MapResponse(ChatCompletionResponse? response)
    {
        if (response is null)
            throw new ChatModelException("The chat endpoint returned an empty response body.");

        if (response.Choices is not { Count: > 0 })
            throw new ChatModelException("The chat response contained no choices.");

        var choice = response.Choices[0];
        var text = choice.Message?.Content ?? string.Empty;

        ChatUsage? usage = null;
        if (response.Usage is { } u)
            usage = new ChatUsage(u.PromptTokens, u.CompletionTokens);

        return new ChatResponse
        {
            Text = text,
            Usage = usage,
            Model = response.Model,
            FinishReason = choice.FinishReason,
        };
    }

    // -----------------------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------------------

    private static HttpClient CreateOwnedClient(OpenAiCompatibleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var client = new HttpClient();
        if (options.Timeout is { } timeout)
            client.Timeout = timeout;
        return client;
    }

    private static async Task<string> ReadTruncatedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return "<unreadable>";
        }

        if (string.IsNullOrEmpty(body))
            return "<empty>";

        return body.Length <= ErrorBodyMaxLength
            ? body
            : body.Substring(0, ErrorBodyMaxLength) + "… (truncated)";
    }
}
