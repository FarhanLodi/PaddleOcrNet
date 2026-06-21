using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// A single completion choice.
/// </summary>
internal sealed class ChatResponseChoice
{
    /// <summary>
    /// The assistant message for this choice.
    /// </summary>
    [JsonPropertyName("message")]
    public ChatResponseMessage? Message { get; set; }

    /// <summary>
    /// Why generation stopped (e.g. <c>stop</c>, <c>length</c>).
    /// </summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}
