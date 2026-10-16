using PaddleOcrNet.Models;

namespace PaddleOcrNet.Pdf;

/// <summary>Source evidence for a selected hybrid PDF line. Native confidence 1 is a sentinel, not a calibrated probability.</summary>
public sealed record PdfLineEvidence
{
    /// <summary>The selected line in rendered-page pixel coordinates.</summary>
    public required OcrLine Selected { get; init; }
    /// <summary>The source of the selected line.</summary>
    public required PdfPageSource Source { get; init; }
    /// <summary>Overlapping native or visual readings retained for review; never inserted as duplicate selectable text.</summary>
    public IReadOnlyList<OcrLine> Alternatives { get; init; } = Array.Empty<OcrLine>();
    /// <summary>Source of each corresponding entry in <see cref="Alternatives"/>.</summary>
    public IReadOnlyList<PdfPageSource> AlternativeSources { get; init; } = Array.Empty<PdfPageSource>();
    /// <summary>True when an overlapping reading disagrees after NFC and whitespace normalization; case is preserved.</summary>
    public bool HasConflict { get; init; }
}
