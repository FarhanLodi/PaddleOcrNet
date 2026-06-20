namespace PaddleOcrNet.Intelligence;

/// <summary>
/// Configuration for <see cref="OpenAiCompatibleChatModel"/> — the endpoint, credentials, default model, and
/// auth/header conventions for any OpenAI-style <c>/chat/completions</c> service (OpenAI, Azure OpenAI,
/// Ollama, vLLM, LM Studio, Groq, Together, DeepSeek, Mistral, …). Build one directly, or use a factory
/// helper (<see cref="OpenAi"/>, <see cref="AzureOpenAi"/>, <see cref="Ollama"/>, <see cref="Generic"/>) for
/// the common providers.
/// </summary>
public sealed record OpenAiCompatibleOptions
{
    /// <summary>
    /// The base URL of the service, including any <c>/v1</c> segment the provider expects
    /// (e.g. <c>https://api.openai.com/v1</c>). If this already ends with <c>/chat/completions</c> it is used
    /// verbatim; otherwise <c>/chat/completions</c> is appended.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// The API key. When <c>null</c> or empty no auth header is sent — suitable for keyless local servers
    /// (Ollama, LM Studio).
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>The default model (or Azure deployment) used when a <see cref="ChatRequest.Model"/> is not set.</summary>
    public required string DefaultModel { get; init; }

    /// <summary>
    /// The header that carries the API key. Default <c>Authorization</c> (OpenAI-style). Azure uses
    /// <c>api-key</c>.
    /// </summary>
    public string ApiKeyHeaderName { get; init; } = "Authorization";

    /// <summary>
    /// The prefix prepended to the API key in the auth header. Default <c>"Bearer "</c> (OpenAI-style). Azure
    /// uses an empty prefix (the raw key in the <c>api-key</c> header).
    /// </summary>
    public string ApiKeyPrefix { get; init; } = "Bearer ";

    /// <summary>Extra headers sent with every request (e.g. <c>OpenAI-Organization</c>); <c>null</c> for none.</summary>
    public IReadOnlyDictionary<string, string>? AdditionalHeaders { get; init; }

    /// <summary>
    /// A query string appended to the endpoint, without a leading <c>?</c> or <c>&amp;</c>
    /// (e.g. <c>api-version=2024-10-21</c> for Azure OpenAI); <c>null</c> for none.
    /// </summary>
    public string? ApiVersionQuery { get; init; }

    /// <summary>
    /// The per-request timeout. Applied only when this instance owns its <see cref="System.Net.Http.HttpClient"/>
    /// (the single-arg <see cref="OpenAiCompatibleChatModel"/> constructor); ignored when a client is supplied
    /// by the caller. <c>null</c> leaves the client default.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Whether the configured model accepts image attachments. Default <c>true</c>.</summary>
    public bool SupportsVision { get; init; } = true;

    /// <summary>
    /// Resolves the full <c>chat/completions</c> request URI from <see cref="BaseUrl"/> and
    /// <see cref="ApiVersionQuery"/>. If <see cref="BaseUrl"/> already ends with <c>/chat/completions</c> it is
    /// used as-is; otherwise <c>/chat/completions</c> is appended (a single slash, regardless of any trailing
    /// slash on the base). The <see cref="ApiVersionQuery"/> is appended when set.
    /// </summary>
    /// <returns>The absolute endpoint URI to POST to.</returns>
    public Uri ResolveEndpoint()
    {
        var trimmed = BaseUrl.TrimEnd('/');
        var path = trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/chat/completions";

        if (!string.IsNullOrEmpty(ApiVersionQuery))
        {
            var separator = path.Contains('?') ? '&' : '?';
            path = path + separator + ApiVersionQuery;
        }

        return new Uri(path, UriKind.Absolute);
    }

    // -----------------------------------------------------------------------------------------------------
    // Provider factory helpers
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Options for the public OpenAI API (<c>https://api.openai.com/v1</c>) with Bearer auth.
    /// </summary>
    /// <param name="apiKey">Your OpenAI API key.</param>
    /// <param name="model">The model to use; defaults to <c>gpt-4o-mini</c>.</param>
    /// <returns>Configured options targeting OpenAI.</returns>
    public static OpenAiCompatibleOptions OpenAi(string apiKey, string model = "gpt-4o-mini") => new()
    {
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = apiKey,
        DefaultModel = model,
    };

    /// <summary>
    /// Options for an Azure OpenAI deployment. The key travels in the <c>api-key</c> header (no prefix) and the
    /// <c>api-version</c> is appended as a query string, per Azure's convention.
    /// </summary>
    /// <param name="endpoint">The resource endpoint, e.g. <c>https://my-resource.openai.azure.com</c> (no trailing path).</param>
    /// <param name="deployment">The deployment name (also used as the default model).</param>
    /// <param name="apiKey">The Azure OpenAI key.</param>
    /// <param name="apiVersion">The Azure API version; defaults to <c>2024-10-21</c>.</param>
    /// <returns>Configured options targeting the Azure OpenAI deployment.</returns>
    public static OpenAiCompatibleOptions AzureOpenAi(
        string endpoint, string deployment, string apiKey, string apiVersion = "2024-10-21") => new()
    {
        BaseUrl = $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}",
        ApiKey = apiKey,
        DefaultModel = deployment,
        ApiKeyHeaderName = "api-key",
        ApiKeyPrefix = "",
        ApiVersionQuery = $"api-version={apiVersion}",
    };

    /// <summary>
    /// Options for a local Ollama server's OpenAI-compatible endpoint. No API key is sent.
    /// </summary>
    /// <param name="model">The Ollama model tag, e.g. <c>llama3.2-vision</c>.</param>
    /// <param name="baseUrl">The Ollama OpenAI base URL; defaults to <c>http://localhost:11434/v1</c>.</param>
    /// <returns>Configured (keyless) options targeting Ollama.</returns>
    public static OpenAiCompatibleOptions Ollama(string model, string baseUrl = "http://localhost:11434/v1") => new()
    {
        BaseUrl = baseUrl,
        ApiKey = null,
        DefaultModel = model,
    };

    /// <summary>
    /// Options for any other OpenAI-compatible service. Sends Bearer auth when <paramref name="apiKey"/> is
    /// non-empty; otherwise keyless.
    /// </summary>
    /// <param name="baseUrl">The service base URL, including any <c>/v1</c> segment.</param>
    /// <param name="apiKey">The API key, or <c>null</c> for keyless servers.</param>
    /// <param name="model">The default model.</param>
    /// <returns>Configured options targeting the given service.</returns>
    public static OpenAiCompatibleOptions Generic(string baseUrl, string? apiKey, string model) => new()
    {
        BaseUrl = baseUrl,
        ApiKey = apiKey,
        DefaultModel = model,
    };
}
