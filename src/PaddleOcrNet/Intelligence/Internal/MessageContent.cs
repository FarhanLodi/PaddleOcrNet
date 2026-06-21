using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// Holds either a plain-text string or an ordered list of multimodal content parts. Serialized by
/// <see cref="MessageContentConverter"/> to the OpenAI shape: a bare string when text-only, or a parts array
/// when images are attached.
/// </summary>
[JsonConverter(typeof(MessageContentConverter))]
internal sealed class MessageContent
{
    /// <summary>
    /// The plain-text content, used when <see cref="Parts"/> is <c>null</c>.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// The multimodal content parts; when non-<c>null</c> these are emitted instead of <see cref="Text"/>.
    /// </summary>
    public List<ContentPart>? Parts { get; set; }
}
