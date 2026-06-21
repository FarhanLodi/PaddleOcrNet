using PaddleOcrNet.Structure;

namespace PaddleOcrNet.Intelligence;

/// <summary>
/// Tunes the <see cref="DocumentIntelligenceEngine"/>: generation parameters for the underlying
/// <see cref="IChatModel"/>, the document-structure analysis pass-through, an optional system-prompt
/// override, and whether to additionally ground the prompt on the page image (vision).
/// </summary>
public sealed record DocumentIntelligenceOptions
{
    /// <summary>
    /// Sampling temperature passed to the chat model. Defaults to <c>0</c> for deterministic, faithful
    /// extraction / answers.
    /// </summary>
    public double Temperature { get; init; } = 0;

    /// <summary>
    /// Maximum tokens the model may generate; <c>null</c> leaves the provider default.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// When <c>true</c> and the chat model reports <see cref="IChatModel.SupportsVision"/> and a page image
    /// is available, the engine ALSO attaches the page image (PNG) to the user message so a multimodal model
    /// can read directly from pixels. This is optional and costs extra tokens; the default is <c>false</c>,
    /// i.e. the prompt is grounded purely on the OCR/structure Markdown.
    /// </summary>
    public bool UseVision { get; init; }

    /// <summary>
    /// Replaces the engine's built-in system instruction for both extraction and question-answering. When
    /// <c>null</c> (the default) the engine uses its task-specific built-in system prompts. Use with care:
    /// the built-in extraction prompt is what makes the JSON reply parseable.
    /// </summary>
    public string? SystemPromptOverride { get; init; }

    /// <summary>
    /// Replaces the engine's built-in system instruction for chart-to-data parsing
    /// (<see cref="IDocumentIntelligence.ParseChartsAsync(string, System.Threading.CancellationToken)"/>).
    /// When null (default), the engine uses its built-in chart-extraction prompt.
    /// </summary>
    public string? ChartExtractionSystemPromptOverride { get; init; }

    /// <summary>
    /// Options forwarded to <see cref="Services.IPaddleOcrService.AnalyzeDocumentAsync(string, StructureOptions?, System.Threading.CancellationToken)"/>
    /// when the engine has to analyze an image/path itself. <c>null</c> uses the analyzer's defaults.
    /// Ignored on the overloads that already receive a <see cref="StructureResult"/>.
    /// </summary>
    public StructureOptions? StructureOptions { get; init; }

    /// <summary>
    /// Default options (temperature 0, text-only, analyzer defaults).
    /// </summary>
    public static DocumentIntelligenceOptions Default { get; } = new();
}
