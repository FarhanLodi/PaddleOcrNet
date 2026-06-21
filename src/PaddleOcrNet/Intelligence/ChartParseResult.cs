using PaddleOcrNet.Models;

namespace PaddleOcrNet.Intelligence;

/// <summary>
/// The result of parsing every chart region in a document into structured data.
/// </summary>
public sealed record ChartParseResult
{
    /// <summary>
    /// One entry per chart region found, in reading order.
    /// </summary>
    public required IReadOnlyList<ParsedChart> Charts { get; init; }
    /// <summary>
    /// Aggregate token usage across all chart calls (sum), when any provider reported usage; else null.
    /// </summary>
    public ChatUsage? Usage { get; init; }
    /// <summary>
    /// The model that produced the parses, when reported.
    /// </summary>
    public string? Model { get; init; }
    /// <summary>
    /// An empty result (no charts found).
    /// </summary>
    public static ChartParseResult Empty { get; } = new() { Charts = Array.Empty<ParsedChart>() };
}
