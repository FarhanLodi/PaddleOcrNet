namespace PaddleOcrNet.Intelligence;

/// <summary>
/// The role of a <see cref="ChatMessage"/> in a conversation.
/// </summary>
public enum ChatRole
{
    /// <summary>
    /// System / developer instruction that steers the model.
    /// </summary>
    System,

    /// <summary>
    /// An end-user (or pipeline) turn — the document text, question, or extraction request.
    /// </summary>
    User,

    /// <summary>
    /// A prior model turn, supplied when continuing a multi-turn exchange.
    /// </summary>
    Assistant,
}
