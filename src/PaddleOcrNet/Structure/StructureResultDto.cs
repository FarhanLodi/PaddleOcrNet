using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddleOcrNet.Structure;

/// <summary>
/// Serializable projection of a <see cref="StructureResult"/> (blocks + source dimensions).
/// </summary>
internal sealed class StructureResultDto
{
    /// <summary>Actual geometry history, when supplied by the structure pipeline.</summary>
    public StructureCoordinateProvenance? Coordinates { get; init; }
    /// <summary>Unassigned source OCR evidence, preserved even in the compact export.</summary>
    public IReadOnlyList<PaddleOcrNet.Models.OcrLine> OrphanLines { get; init; } = Array.Empty<PaddleOcrNet.Models.OcrLine>();
    /// <summary>
    /// Width (px) of the source image, or 0 if unknown.
    /// </summary>
    public int SourceWidth { get; init; }

    /// <summary>
    /// Height (px) of the source image, or 0 if unknown.
    /// </summary>
    public int SourceHeight { get; init; }

    /// <summary>
    /// The analyzed blocks in reading order.
    /// </summary>
    public IReadOnlyList<StructureBlockDto> Blocks { get; init; } = Array.Empty<StructureBlockDto>();
}
