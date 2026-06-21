using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// The <c>image_url</c> object holding a data URL.
/// </summary>
internal sealed class ImageUrl
{
    /// <summary>
    /// The image URL — here always a <c>data:{mediaType};base64,{data}</c> URL.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
