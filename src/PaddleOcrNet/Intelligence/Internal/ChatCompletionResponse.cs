using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// The OpenAI <c>chat/completions</c> response body.
/// </summary>
internal sealed class ChatCompletionResponse
{
    /// <summary>
    /// The model that produced the completion.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// The completion choices; the first is used.
    /// </summary>
    [JsonPropertyName("choices")]
    public List<ChatResponseChoice>? Choices { get; set; }

    /// <summary>
    /// Token accounting, when reported.
    /// </summary>
    [JsonPropertyName("usage")]
    public ChatUsageDto? Usage { get; set; }
}
