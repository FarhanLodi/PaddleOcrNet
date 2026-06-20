namespace PaddleOcrNet.Intelligence;

/// <summary>
/// The role of a <see cref="ChatMessage"/> in a conversation.
/// </summary>
public enum ChatRole
{
    /// <summary>System / developer instruction that steers the model.</summary>
    System,

    /// <summary>An end-user (or pipeline) turn — the document text, question, or extraction request.</summary>
    User,

    /// <summary>A prior model turn, supplied when continuing a multi-turn exchange.</summary>
    Assistant,
}

/// <summary>
/// An image attached to a <see cref="ChatMessage"/> for multimodal (vision) models. Used by the document
/// intelligence pipeline when it sends page/region crops to a vision-capable model (e.g. for chart parsing
/// or when no reliable OCR text is available).
/// </summary>
/// <param name="Data">The raw image bytes (PNG/JPEG/…).</param>
/// <param name="MediaType">The IANA media type, e.g. <c>image/png</c> or <c>image/jpeg</c>.</param>
public sealed record ChatImage(ReadOnlyMemory<byte> Data, string MediaType);

/// <summary>
/// A single message in a chat request. Carries text and, optionally, one or more <see cref="Images"/> for
/// vision models. Use the <see cref="System"/>, <see cref="User"/>, and <see cref="Assistant"/> factory
/// helpers for the common cases.
/// </summary>
public sealed record ChatMessage
{
    /// <summary>The message role.</summary>
    public required ChatRole Role { get; init; }

    /// <summary>The text content (may be empty when the message carries only images).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Optional image attachments for multimodal models; <c>null</c> for text-only messages.</summary>
    public IReadOnlyList<ChatImage>? Images { get; init; }

    /// <summary>Creates a <see cref="ChatRole.System"/> message.</summary>
    public static ChatMessage System(string text) => new() { Role = ChatRole.System, Text = text };

    /// <summary>Creates a <see cref="ChatRole.User"/> message, optionally with image attachments.</summary>
    public static ChatMessage User(string text, IReadOnlyList<ChatImage>? images = null) =>
        new() { Role = ChatRole.User, Text = text, Images = images };

    /// <summary>Creates a <see cref="ChatRole.Assistant"/> message.</summary>
    public static ChatMessage Assistant(string text) => new() { Role = ChatRole.Assistant, Text = text };
}

/// <summary>
/// A provider-agnostic chat-completion request. The same request shape is honored by every
/// <see cref="IChatModel"/> implementation (the built-in OpenAI-compatible adapter or a caller's own),
/// so switching providers never changes the calling code.
/// </summary>
public sealed record ChatRequest
{
    /// <summary>The conversation, in order. Typically a system instruction followed by a user turn.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Model name override for this call (e.g. <c>gpt-4o-mini</c>, <c>qwen2.5-vl</c>). When <c>null</c> the
    /// <see cref="IChatModel"/> uses its configured default model.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>Sampling temperature (0 = deterministic). <c>null</c> leaves the provider default.</summary>
    public double? Temperature { get; init; }

    /// <summary>Maximum tokens to generate. <c>null</c> leaves the provider default.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// When <c>true</c>, asks the model to emit a single well-formed JSON object (OpenAI's
    /// <c>response_format: json_object</c>). The document-intelligence engine sets this for key-information
    /// extraction so the reply is machine-parseable. Providers that don't support it should ignore it.
    /// </summary>
    public bool JsonMode { get; init; }
}

/// <summary>Token accounting for a completion, when the provider reports it.</summary>
/// <param name="PromptTokens">Tokens in the request.</param>
/// <param name="CompletionTokens">Tokens generated.</param>
public sealed record ChatUsage(int PromptTokens, int CompletionTokens)
{
    /// <summary>Total tokens billed (prompt + completion).</summary>
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>The result of a chat completion.</summary>
public sealed record ChatResponse
{
    /// <summary>The generated assistant text (the JSON object string when <see cref="ChatRequest.JsonMode"/> was set).</summary>
    public required string Text { get; init; }

    /// <summary>Token usage, when reported by the provider; otherwise <c>null</c>.</summary>
    public ChatUsage? Usage { get; init; }

    /// <summary>The model that produced the response, when reported.</summary>
    public string? Model { get; init; }

    /// <summary>The provider's finish reason (e.g. <c>stop</c>, <c>length</c>), when reported.</summary>
    public string? FinishReason { get; init; }
}

/// <summary>
/// A provider-agnostic large-language-model client. This is the single seam the document-intelligence
/// features (key-information extraction, document Q&amp;A) depend on, so any provider can be plugged in by
/// implementing this one interface. A ready-made OpenAI-compatible implementation
/// (<c>OpenAiCompatibleChatModel</c>) ships in the box and targets OpenAI, Azure OpenAI, Ollama, vLLM,
/// LM Studio, Groq, Together, DeepSeek, Mistral, and any other OpenAI-style <c>/chat/completions</c> endpoint.
/// </summary>
public interface IChatModel
{
    /// <summary>
    /// Completes <paramref name="request"/> and returns the model's reply.
    /// </summary>
    /// <param name="request">The conversation and generation options.</param>
    /// <param name="cancellationToken">Cancels the in-flight provider call.</param>
    /// <returns>The completion text plus any usage/metadata the provider reported.</returns>
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this model accepts image attachments (<see cref="ChatMessage.Images"/>). The engine uses this
    /// to decide between a text-only (OCR-grounded) prompt and a multimodal one.
    /// </summary>
    bool SupportsVision { get; }
}
