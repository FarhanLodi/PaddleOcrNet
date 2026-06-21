namespace PaddleOcrNet.Intelligence;

/// <summary>
/// A single message in a chat request. Carries text and, optionally, one or more <see cref="Images"/> for
/// vision models. Use the <see cref="System"/>, <see cref="User"/>, and <see cref="Assistant"/> factory
/// helpers for the common cases.
/// </summary>
public sealed record ChatMessage
{
    /// <summary>
    /// The message role.
    /// </summary>
    public required ChatRole Role { get; init; }

    /// <summary>
    /// The text content (may be empty when the message carries only images).
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Optional image attachments for multimodal models; <c>null</c> for text-only messages.
    /// </summary>
    public IReadOnlyList<ChatImage>? Images { get; init; }

    /// <summary>
    /// Creates a <see cref="ChatRole.System"/> message.
    /// </summary>
    public static ChatMessage System(string text) => new() { Role = ChatRole.System, Text = text };

    /// <summary>
    /// Creates a <see cref="ChatRole.User"/> message, optionally with image attachments.
    /// </summary>
    public static ChatMessage User(string text, IReadOnlyList<ChatImage>? images = null) =>
        new() { Role = ChatRole.User, Text = text, Images = images };

    /// <summary>
    /// Creates a <see cref="ChatRole.Assistant"/> message.
    /// </summary>
    public static ChatMessage Assistant(string text) => new() { Role = ChatRole.Assistant, Text = text };
}
