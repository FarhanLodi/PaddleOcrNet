namespace PaddleOcrNet.Intelligence;

/// <summary>
/// A provider-agnostic chat-completion request. The same request shape is honored by every
/// <see cref="IChatModel"/> implementation (the built-in OpenAI-compatible adapter or a caller's own),
/// so switching providers never changes the calling code.
/// </summary>
public sealed record ChatRequest
{
    /// <summary>
    /// The conversation, in order. Typically a system instruction followed by a user turn.
    /// </summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Model name override for this call (e.g. <c>gpt-4o-mini</c>, <c>qwen2.5-vl</c>). When <c>null</c> the
    /// <see cref="IChatModel"/> uses its configured default model.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Sampling temperature (0 = deterministic). <c>null</c> leaves the provider default.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Maximum tokens to generate. <c>null</c> leaves the provider default.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// When <c>true</c>, asks the model to emit a single well-formed JSON object (OpenAI's
    /// <c>response_format: json_object</c>). The document-intelligence engine sets this for key-information
    /// extraction so the reply is machine-parseable. Providers that don't support it should ignore it.
    /// </summary>
    public bool JsonMode { get; init; }
}
