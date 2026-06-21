namespace PaddleOcrNet.Intelligence;

/// <summary>
/// Token accounting for a completion, when the provider reports it.
/// </summary>
/// <param name="PromptTokens">Tokens in the request.</param>
/// <param name="CompletionTokens">Tokens generated.</param>
public sealed record ChatUsage(int PromptTokens, int CompletionTokens)
{
    /// <summary>
    /// Total tokens billed (prompt + completion).
    /// </summary>
    public int TotalTokens => PromptTokens + CompletionTokens;
}
