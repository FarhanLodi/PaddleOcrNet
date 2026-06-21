using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// The <c>usage</c> object reported by the provider.
/// </summary>
internal sealed class ChatUsageDto
{
    /// <summary>
    /// Tokens in the prompt.
    /// </summary>
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    /// <summary>
    /// Tokens generated in the completion.
    /// </summary>
    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    /// <summary>
    /// Total tokens billed.
    /// </summary>
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}
