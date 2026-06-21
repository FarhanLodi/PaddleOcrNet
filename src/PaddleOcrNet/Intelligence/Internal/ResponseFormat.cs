using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// The <c>response_format</c> object (only <c>type</c> is used here).
/// </summary>
internal sealed class ResponseFormat
{
    /// <summary>
    /// The format type, e.g. <c>json_object</c>.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}
