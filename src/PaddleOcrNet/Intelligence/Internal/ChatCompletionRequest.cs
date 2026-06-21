using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// The OpenAI <c>chat/completions</c> request body.
/// </summary>
internal sealed class ChatCompletionRequest
{
    /// <summary>
    /// The model name (deployment name on Azure).
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// The conversation, oldest first.
    /// </summary>
    [JsonPropertyName("messages")]
    public List<ChatRequestMessage> Messages { get; set; } = new();

    /// <summary>
    /// Sampling temperature; omitted when <c>null</c>.
    /// </summary>
    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    /// <summary>
    /// Maximum tokens to generate; omitted when <c>null</c>.
    /// </summary>
    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    /// <summary>
    /// Response-format selector; set to <c>json_object</c> for JSON mode, otherwise omitted.
    /// </summary>
    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseFormat? ResponseFormat { get; set; }
}
