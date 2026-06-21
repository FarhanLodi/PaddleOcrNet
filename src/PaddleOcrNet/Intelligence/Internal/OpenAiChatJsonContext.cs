using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// Source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> for the
/// OpenAI-compatible request/response DTOs, so the adapter can (de)serialize with no reflection and stay
/// trim-/Native-AOT-safe.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatRequestMessage))]
[JsonSerializable(typeof(ContentPart))]
[JsonSerializable(typeof(List<ContentPart>))]
[JsonSerializable(typeof(ImageUrl))]
[JsonSerializable(typeof(ResponseFormat))]
internal partial class OpenAiChatJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
