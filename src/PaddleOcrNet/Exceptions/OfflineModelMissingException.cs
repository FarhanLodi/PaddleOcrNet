namespace PaddleOcrNet;

/// <summary>
/// A required model is not present in the cache and strict offline mode is enabled, so it cannot be
/// downloaded. Pre-seed the cache or disable <c>ModelDownloadOptions.Offline</c>.
/// </summary>
public sealed class OfflineModelMissingException : PaddleOcrException
{
    /// <summary>
    /// Initializes a new instance with a message.
    /// </summary>
    public OfflineModelMissingException(string message) : base(message) { }
}
