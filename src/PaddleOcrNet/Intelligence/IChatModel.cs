namespace PaddleOcrNet.Intelligence;

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
