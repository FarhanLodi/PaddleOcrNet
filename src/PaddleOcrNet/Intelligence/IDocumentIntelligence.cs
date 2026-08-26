using PaddleOcrNet.Structure;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace PaddleOcrNet.Intelligence;

/// <summary>
/// The document-intelligence engine: provider-agnostic Key-Information Extraction (KIE) and document
/// question-answering over a document image. It first analyzes the document's structure (layout, tables,
/// reading order) with the injected OCR service, serializes it to Markdown, and grounds an injected
/// <see cref="IChatModel"/> on that Markdown — so any LLM provider can drive extraction / Q&amp;A without the
/// calling code changing. This is the provider-agnostic replacement for PaddleOCR's PP-ChatOCR.
/// </summary>
public interface IDocumentIntelligence
{
    /// <summary>
    /// Extracts each of the requested <paramref name="keys"/> from the document image at
    /// <paramref name="imagePath"/>. Runs structure analysis first, then KIE.
    /// </summary>
    /// <param name="imagePath">Path to the document image on disk.</param>
    /// <param name="keys">The field names to extract (echoed back verbatim in the result).</param>
    /// <param name="cancellationToken">Cancels the analysis and the model call.</param>
    /// <returns>The extracted fields, one per requested key.</returns>
    Task<KeyInformationResult> ExtractKeyInformationAsync(string imagePath, IReadOnlyList<string> keys, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts each of the requested <paramref name="keys"/> from an already-decoded document
    /// <paramref name="image"/>. The caller retains ownership of the image. Runs structure analysis first,
    /// then KIE.
    /// </summary>
    /// <param name="image">The decoded document image.</param>
    /// <param name="keys">The field names to extract (echoed back verbatim in the result).</param>
    /// <param name="cancellationToken">Cancels the analysis and the model call.</param>
    /// <returns>The extracted fields, one per requested key.</returns>
    Task<KeyInformationResult> ExtractKeyInformationAsync(Image<Rgb24> image, IReadOnlyList<string> keys, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts each of the requested <paramref name="keys"/> from a document whose structure has already
    /// been analyzed. The document is serialized to Markdown and used to ground the model.
    /// </summary>
    /// <param name="document">The analyzed document structure.</param>
    /// <param name="keys">The field names to extract (echoed back verbatim in the result).</param>
    /// <param name="cancellationToken">Cancels the model call.</param>
    /// <returns>The extracted fields, one per requested key.</returns>
    Task<KeyInformationResult> ExtractKeyInformationAsync(StructureResult document, IReadOnlyList<string> keys, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers <paramref name="question"/> from the document image at <paramref name="imagePath"/>. Runs
    /// structure analysis first, then question-answering grounded only on the document.
    /// </summary>
    /// <param name="imagePath">Path to the document image on disk.</param>
    /// <param name="question">The natural-language question about the document.</param>
    /// <param name="cancellationToken">Cancels the analysis and the model call.</param>
    /// <returns>The model's answer, grounded in the document.</returns>
    Task<DocumentAnswer> AskAsync(string imagePath, string question, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers <paramref name="question"/> from a document whose structure has already been analyzed,
    /// grounded only on the document.
    /// </summary>
    /// <param name="document">The analyzed document structure.</param>
    /// <param name="question">The natural-language question about the document.</param>
    /// <param name="cancellationToken">Cancels the model call.</param>
    /// <returns>The model's answer, grounded in the document.</returns>
    Task<DocumentAnswer> AskAsync(StructureResult document, string question, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the underlying data of every chart/plot region in the document image at
    /// <paramref name="imagePath"/>. Runs structure analysis first, then crops each detected chart region and
    /// sends its pixels to the injected <see cref="IChatModel"/> to reconstruct the chart's data as a
    /// Markdown table. This is the provider-agnostic equivalent of PaddleOCR's PP-Chart2Table.
    /// <para>
    /// Chart parsing REQUIRES a vision-capable <see cref="IChatModel"/> (the cropped chart pixels are sent to
    /// the model): if the configured model reports <see cref="IChatModel.SupportsVision"/> = <c>false</c> and
    /// the document contains at least one chart region, a <see cref="NotSupportedException"/> is thrown.
    /// </para>
    /// </summary>
    /// <param name="imagePath">Path to the document image on disk.</param>
    /// <param name="cancellationToken">Cancels the analysis and the model calls.</param>
    /// <returns>One parsed entry per chart region, in reading order; <see cref="ChartParseResult.Empty"/> when none.</returns>
    Task<ChartParseResult> ParseChartsAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the underlying data of every chart/plot region in an already-decoded document
    /// <paramref name="image"/>. The caller retains ownership of the image. Runs structure analysis first,
    /// then crops each detected chart region and sends its pixels to the injected <see cref="IChatModel"/> to
    /// reconstruct the chart's data as a Markdown table.
    /// <para>
    /// Chart parsing REQUIRES a vision-capable <see cref="IChatModel"/> (the cropped chart pixels are sent to
    /// the model): if the configured model reports <see cref="IChatModel.SupportsVision"/> = <c>false</c> and
    /// the document contains at least one chart region, a <see cref="NotSupportedException"/> is thrown.
    /// </para>
    /// </summary>
    /// <param name="image">The decoded document image.</param>
    /// <param name="cancellationToken">Cancels the analysis and the model calls.</param>
    /// <returns>One parsed entry per chart region, in reading order; <see cref="ChartParseResult.Empty"/> when none.</returns>
    Task<ChartParseResult> ParseChartsAsync(Image<Rgb24> image, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the underlying data of every chart/plot region in a document whose structure has already been
    /// analyzed. The <paramref name="image"/> is still required because the chart pixels are cropped from it
    /// and sent to the injected <see cref="IChatModel"/> to reconstruct the chart's data as a Markdown table;
    /// reuse this overload to avoid re-analyzing a document you have already structured.
    /// <para>
    /// Chart parsing REQUIRES a vision-capable <see cref="IChatModel"/> (the cropped chart pixels are sent to
    /// the model): if the configured model reports <see cref="IChatModel.SupportsVision"/> = <c>false</c> and
    /// the document contains at least one chart region, a <see cref="NotSupportedException"/> is thrown.
    /// </para>
    /// </summary>
    /// <param name="document">The analyzed document structure (its chart blocks are parsed).</param>
    /// <param name="image">The decoded document image the chart pixels are cropped from. The caller retains ownership.</param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>One parsed entry per chart region, in reading order; <see cref="ChartParseResult.Empty"/> when none.</returns>
    Task<ChartParseResult> ParseChartsAsync(StructureResult document, Image<Rgb24> image, CancellationToken cancellationToken = default);
}
