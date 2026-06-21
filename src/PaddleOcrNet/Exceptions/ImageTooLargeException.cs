namespace PaddleOcrNet;

/// <summary>
/// An input image's pixel count exceeds <c>PaddleOcrServiceOptions.MaxImagePixels</c>, the
/// decompression-bomb / pixel-flood guard. Raise the limit or downscale the image.
/// </summary>
public sealed class ImageTooLargeException : PaddleOcrException
{
    /// <summary>
    /// Initializes a new instance with a message.
    /// </summary>
    public ImageTooLargeException(string message) : base(message) { }
}
