namespace PaddleOcrNet.Intelligence;

/// <summary>
/// The result of a document question-answering call: the model's natural-language <see cref="Answer"/>
/// (grounded in the analyzed document) plus the usage/model metadata the provider reported. When the
/// document does not contain the answer, <see cref="Answer"/> carries the model's stated inability to find
/// it rather than a fabricated value.
/// </summary>
public sealed record DocumentAnswer
{
    /// <summary>The answer text the model produced from the document.</summary>
    public required string Answer { get; init; }

    /// <summary>Token usage for the answer call, when the provider reported it; otherwise <c>null</c>.</summary>
    public ChatUsage? Usage { get; init; }

    /// <summary>The model that produced the answer, when reported by the provider; otherwise <c>null</c>.</summary>
    public string? Model { get; init; }
}
