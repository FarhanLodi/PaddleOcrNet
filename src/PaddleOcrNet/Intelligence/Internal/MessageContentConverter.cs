using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

/// <summary>
/// Serializes <see cref="MessageContent"/> to the OpenAI wire shape: a bare JSON string when the message is
/// text-only (<see cref="MessageContent.Parts"/> is <c>null</c>), or an array of content parts when images
/// are attached. Each part is written through the source-generated context so the converter stays
/// reflection-free and Native-AOT-safe. Deserialization is not needed (the adapter never reads a request
/// body) and throws.
/// </summary>
internal sealed class MessageContentConverter : JsonConverter<MessageContent>
{
    /// <inheritdoc />
    public override MessageContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("MessageContent is write-only on the request path.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, MessageContent value, JsonSerializerOptions options)
    {
        if (value.Parts is null)
        {
            writer.WriteStringValue(value.Text ?? string.Empty);
            return;
        }

        writer.WriteStartArray();
        foreach (var part in value.Parts)
            JsonSerializer.Serialize(writer, part, OpenAiChatJsonContext.Default.ContentPart);
        writer.WriteEndArray();
    }
}
