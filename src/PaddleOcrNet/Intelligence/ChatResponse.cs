namespace PaddleOcrNet.Intelligence;

/// <summary>
/// The result of a chat completion.
/// </summary>
public sealed record ChatResponse
{
    /// <summary>
    /// The generated assistant text (the JSON object string when <see cref="ChatRequest.JsonMode"/> was set).
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Token usage, when reported by the provider; otherwise <c>null</c>.
    /// </summary>
    public ChatUsage? Usage { get; init; }

    /// <summary>
    /// The model that produced the response, when reported.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// The provider's finish reason (e.g. <c>stop</c>, <c>length</c>), when reported.
    /// </summary>
    public string? FinishReason { get; init; }
}
