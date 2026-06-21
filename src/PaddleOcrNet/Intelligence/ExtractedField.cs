namespace PaddleOcrNet.Intelligence;

/// <summary>
/// One extracted document field: a requested key together with the value the model found for it.
/// <see cref="Value"/> is <c>null</c> when the field was absent from the document (or the model returned
/// JSON <c>null</c> for it). No per-field confidence is exposed — chat LLMs do not reliably produce
/// calibrated per-field confidences, so reporting one would be misleading.
/// </summary>
/// <param name="Key">The requested key, verbatim as passed to the extractor.</param>
/// <param name="Value">The extracted string value, or <c>null</c> when not found in the document.</param>
public sealed record ExtractedField(string Key, string? Value);
