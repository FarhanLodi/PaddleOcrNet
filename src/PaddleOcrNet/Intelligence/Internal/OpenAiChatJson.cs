using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Intelligence.Internal;

// ---------------------------------------------------------------------------------------------------------
// Request DTOs
// ---------------------------------------------------------------------------------------------------------

/// <summary>The OpenAI <c>chat/completions</c> request body.</summary>
internal sealed class ChatCompletionRequest
{
    /// <summary>The model name (deployment name on Azure).</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>The conversation, oldest first.</summary>
    [JsonPropertyName("messages")]
    public List<ChatRequestMessage> Messages { get; set; } = new();

    /// <summary>Sampling temperature; omitted when <c>null</c>.</summary>
    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    /// <summary>Maximum tokens to generate; omitted when <c>null</c>.</summary>
    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    /// <summary>Response-format selector; set to <c>json_object</c> for JSON mode, otherwise omitted.</summary>
    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseFormat? ResponseFormat { get; set; }
}

/// <summary>The <c>response_format</c> object (only <c>type</c> is used here).</summary>
internal sealed class ResponseFormat
{
    /// <summary>The format type, e.g. <c>json_object</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// A single request message. <see cref="Content"/> is serialized as a JSON value that is either a plain
/// string (text-only) or an array of content parts (multimodal). The custom converter on
/// <see cref="MessageContent"/> picks the right shape.
/// </summary>
internal sealed class ChatRequestMessage
{
    /// <summary>The role: <c>system</c>, <c>user</c>, or <c>assistant</c>.</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    /// <summary>The message content (a string, or an array of multimodal parts).</summary>
    [JsonPropertyName("content")]
    public MessageContent Content { get; set; } = new();
}

/// <summary>
/// Holds either a plain-text string or an ordered list of multimodal content parts. Serialized by
/// <see cref="MessageContentConverter"/> to the OpenAI shape: a bare string when text-only, or a parts array
/// when images are attached.
/// </summary>
[JsonConverter(typeof(MessageContentConverter))]
internal sealed class MessageContent
{
    /// <summary>The plain-text content, used when <see cref="Parts"/> is <c>null</c>.</summary>
    public string? Text { get; set; }

    /// <summary>The multimodal content parts; when non-<c>null</c> these are emitted instead of <see cref="Text"/>.</summary>
    public List<ContentPart>? Parts { get; set; }
}

/// <summary>A single multimodal content part: either a text part or an image-URL part.</summary>
internal sealed class ContentPart
{
    /// <summary>The part type: <c>text</c> or <c>image_url</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>The text payload, present when <see cref="Type"/> is <c>text</c>.</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>The image payload, present when <see cref="Type"/> is <c>image_url</c>.</summary>
    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageUrl? ImageUrl { get; set; }
}

/// <summary>The <c>image_url</c> object holding a data URL.</summary>
internal sealed class ImageUrl
{
    /// <summary>The image URL — here always a <c>data:{mediaType};base64,{data}</c> URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

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

// ---------------------------------------------------------------------------------------------------------
// Response DTOs
// ---------------------------------------------------------------------------------------------------------

/// <summary>The OpenAI <c>chat/completions</c> response body.</summary>
internal sealed class ChatCompletionResponse
{
    /// <summary>The model that produced the completion.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>The completion choices; the first is used.</summary>
    [JsonPropertyName("choices")]
    public List<ChatResponseChoice>? Choices { get; set; }

    /// <summary>Token accounting, when reported.</summary>
    [JsonPropertyName("usage")]
    public ChatUsageDto? Usage { get; set; }
}

/// <summary>A single completion choice.</summary>
internal sealed class ChatResponseChoice
{
    /// <summary>The assistant message for this choice.</summary>
    [JsonPropertyName("message")]
    public ChatResponseMessage? Message { get; set; }

    /// <summary>Why generation stopped (e.g. <c>stop</c>, <c>length</c>).</summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>The assistant message inside a response choice.</summary>
internal sealed class ChatResponseMessage
{
    /// <summary>The assistant role (usually <c>assistant</c>).</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>The generated text content.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

/// <summary>The <c>usage</c> object reported by the provider.</summary>
internal sealed class ChatUsageDto
{
    /// <summary>Tokens in the prompt.</summary>
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    /// <summary>Tokens generated in the completion.</summary>
    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    /// <summary>Total tokens billed.</summary>
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

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
