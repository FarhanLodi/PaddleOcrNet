using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// A single multimodal content part: either a text part or an image-URL part.
/// </summary>
internal sealed class ContentPart
{
    /// <summary>
    /// The part type: <c>text</c> or <c>image_url</c>.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// The text payload, present when <see cref="Type"/> is <c>text</c>.
    /// </summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>
    /// The image payload, present when <see cref="Type"/> is <c>image_url</c>.
    /// </summary>
    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageUrl? ImageUrl { get; set; }
}
