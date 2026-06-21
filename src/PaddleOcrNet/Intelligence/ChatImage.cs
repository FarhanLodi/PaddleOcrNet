namespace PaddleOcrNet.Intelligence;

/// <summary>
/// An image attached to a <see cref="ChatMessage"/> for multimodal (vision) models. Used by the document
/// intelligence pipeline when it sends page/region crops to a vision-capable model (e.g. for chart parsing
/// or when no reliable OCR text is available).
/// </summary>
/// <param name="Data">The raw image bytes (PNG/JPEG/…).</param>
/// <param name="MediaType">The IANA media type, e.g. <c>image/png</c> or <c>image/jpeg</c>.</param>
public sealed record ChatImage(ReadOnlyMemory<byte> Data, string MediaType);
