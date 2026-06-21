using PaddleOcrNet.Models;

namespace PaddleOcrNet.Intelligence;

/// <summary>
/// One chart region parsed into its underlying data by a vision model.
/// </summary>
public sealed record ParsedChart
{
    /// <summary>
    /// Reading-order index of the source chart block.
    /// </summary>
    public required int Order { get; init; }
    /// <summary>
    /// Axis-aligned bounds of the chart region (source-image pixels).
    /// </summary>
    public required OcrBoundingBox Bounds { get; init; }
    /// <summary>
    /// The chart kind the model inferred (e.g. "bar", "line", "pie"); null if not reported.
    /// </summary>
    public string? ChartType { get; init; }
    /// <summary>
    /// The chart title, when present.
    /// </summary>
    public string? Title { get; init; }
    /// <summary>
    /// The chart's underlying data as a GitHub-flavored Markdown table (empty when none recovered).
    /// </summary>
    public string DataMarkdown { get; init; } = string.Empty;
    /// <summary>
    /// A short natural-language description/summary of the chart, when the model provided one.
    /// </summary>
    public string? Description { get; init; }
    /// <summary>
    /// The raw JSON object text the model returned for this chart (after fence/prose stripping), or null.
    /// </summary>
    public string? RawJson { get; init; }
    /// <summary>
    /// Token usage for this chart's model call, when reported.
    /// </summary>
    public ChatUsage? Usage { get; init; }
    /// <summary>
    /// The model that parsed this chart, when reported.
    /// </summary>
    public string? Model { get; init; }
}
