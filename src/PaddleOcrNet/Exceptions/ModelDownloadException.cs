namespace PaddleOcrNet;

/// <summary>
/// A model file could not be obtained — a network/IO failure while downloading, a rejected
/// (non-HTTPS / malformed) source, or a refused file name. Derives <see cref="PaddleOcrException"/>
/// so existing catch-all handlers keep working.
/// </summary>
public class ModelDownloadException : PaddleOcrException
{
    /// <summary>
    /// Initializes a new instance with a message.
    /// </summary>
    public ModelDownloadException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance with a message and inner exception.
    /// </summary>
    public ModelDownloadException(string message, Exception innerException) : base(message, innerException) { }
}
