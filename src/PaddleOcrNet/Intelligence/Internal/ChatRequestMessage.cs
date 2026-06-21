using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// A single request message. <see cref="Content"/> is serialized as a JSON value that is either a plain
/// string (text-only) or an array of content parts (multimodal). The custom converter on
/// <see cref="MessageContent"/> picks the right shape.
/// </summary>
internal sealed class ChatRequestMessage
{
    /// <summary>
    /// The role: <c>system</c>, <c>user</c>, or <c>assistant</c>.
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// The message content (a string, or an array of multimodal parts).
    /// </summary>
    [JsonPropertyName("content")]
    public MessageContent Content { get; set; } = new();
}
