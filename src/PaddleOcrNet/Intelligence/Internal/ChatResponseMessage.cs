using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// The assistant message inside a response choice.
/// </summary>
internal sealed class ChatResponseMessage
{
    /// <summary>
    /// The assistant role (usually <c>assistant</c>).
    /// </summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>
    /// The generated text content.
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}
