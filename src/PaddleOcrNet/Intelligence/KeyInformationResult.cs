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

/// <summary>
/// The result of a key-information-extraction (KIE) call: one <see cref="ExtractedField"/> per requested
/// key (in request order), plus the model's raw JSON reply and usage/model metadata. Index or
/// <see cref="TryGet"/> by key for convenient access to a single value.
/// </summary>
public sealed record KeyInformationResult
{
    /// <summary>The extracted fields, one per requested key, in the order the keys were requested.</summary>
    public required IReadOnlyList<ExtractedField> Fields { get; init; }

    /// <summary>
    /// The raw JSON object text the model returned (after stripping any code fences / surrounding prose),
    /// or <c>null</c> when no JSON object could be located in the reply.
    /// </summary>
    public string? RawJson { get; init; }

    /// <summary>Token usage for the extraction call, when the provider reported it; otherwise <c>null</c>.</summary>
    public ChatUsage? Usage { get; init; }

    /// <summary>The model that produced the extraction, when reported by the provider; otherwise <c>null</c>.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets the value extracted for <paramref name="key"/>, or <c>null</c> when the key was not requested or
    /// not found. Matching is ordinal and case-sensitive (keys are echoed back verbatim).
    /// </summary>
    /// <param name="key">The requested key to look up.</param>
    /// <returns>The extracted value, or <c>null</c>.</returns>
    public string? this[string key]
    {
        get
        {
            foreach (var field in Fields)
            {
                if (string.Equals(field.Key, key, StringComparison.Ordinal))
                    return field.Value;
            }

            return null;
        }
    }

    /// <summary>
    /// Tries to get the value extracted for <paramref name="key"/>. Returns <c>true</c> when the key was one
    /// of the requested fields (even if its value is <c>null</c> because it was absent from the document),
    /// and <c>false</c> when the key was never requested.
    /// </summary>
    /// <param name="key">The requested key to look up.</param>
    /// <param name="value">The extracted value (may be <c>null</c> when the field was not found).</param>
    /// <returns><c>true</c> if <paramref name="key"/> was an extracted field; otherwise <c>false</c>.</returns>
    public bool TryGet(string key, out string? value)
    {
        foreach (var field in Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal))
            {
                value = field.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
