using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PaddleOcrNet.Intelligence.Internal;

namespace PaddleOcrNet.Intelligence;

/// <summary>
/// Thrown when an OpenAI-compatible chat endpoint returns a non-success HTTP status or an otherwise
/// unusable response. Carries the HTTP <see cref="StatusCode"/> (when known) and a truncated copy of the
/// response body in the message for diagnostics.
/// </summary>
public sealed class ChatModelException : Exception
{
    /// <summary>
    /// The HTTP status code the provider returned, when the failure came from an HTTP response.
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// Creates a <see cref="ChatModelException"/> with a message and optional HTTP status code.
    /// </summary>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="statusCode">The HTTP status code, when applicable.</param>
    public ChatModelException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Creates a <see cref="ChatModelException"/> wrapping an inner exception.
    /// </summary>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="innerException">The underlying cause.</param>
    /// <param name="statusCode">The HTTP status code, when applicable.</param>
    public ChatModelException(string message, Exception innerException, int? statusCode = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
