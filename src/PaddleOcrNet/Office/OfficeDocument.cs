using System.Text.Json;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Office;

/// <summary>Supported non-macro Office Open XML input formats.</summary>
public enum OfficeDocumentFormat
{
    /// <summary>Wordprocessing document.</summary>
    Docx,
    /// <summary>Spreadsheet workbook.</summary>
    Xlsx,
    /// <summary>Slide presentation.</summary>
    Pptx,
}

/// <summary>Origin and structure of an extracted content block.</summary>
public enum OfficeContentKind
{
    /// <summary>Native paragraph text.</summary>
    Paragraph,
    /// <summary>Native table with explicit cells.</summary>
    Table,
    /// <summary>Spreadsheet cell, including raw cached values.</summary>
    Cell,
    /// <summary>Related embedded image; text is populated only when OCR runs.</summary>
    Image,
}

/// <summary>A native table cell; indices are one-based and are not pixel coordinates.</summary>
public sealed record OfficeTableCell
{
    /// <summary>One-based row.</summary>
    public int Row { get; init; }
    /// <summary>One-based column.</summary>
    public int Column { get; init; }
    /// <summary>Number of rows covered.</summary>
    public int RowSpan { get; init; } = 1;
    /// <summary>Number of columns covered.</summary>
    public int ColumnSpan { get; init; } = 1;
    /// <summary>Unformatted source text or cached cell value.</summary>
    public string Text { get; init; } = "";
}

/// <summary>Native content or image OCR evidence; no Office page geometry is inferred.</summary>
public sealed record OfficeContentBlock
{
    /// <summary>Content origin/type.</summary>
    public OfficeContentKind Kind { get; init; }
    /// <summary>Native text, raw cached spreadsheet value, or embedded-image OCR text.</summary>
    public string Text { get; init; } = "";
    /// <summary>Package-relative embedded image path, never an external URL.</summary>
    public string? ImagePart { get; init; }
    /// <summary>Spreadsheet address, where available.</summary>
    public string? CellReference { get; init; }
    /// <summary>Stored spreadsheet formula expression; never evaluated by this library.</summary>
    public string? Formula { get; init; }
    /// <summary>Native table/cell grid evidence, including spans.</summary>
    public IReadOnlyList<OfficeTableCell> Cells { get; init; } = Array.Empty<OfficeTableCell>();
    /// <summary>OCR evidence in the decoded embedded image's coordinate frame, not Office page coordinates.</summary>
    public OcrResult? ImageOcr { get; init; }
}

/// <summary>A DOCX story, XLSX worksheet or PPTX slide; Word stories are not rendered pages.</summary>
public sealed record OfficeDocumentSection
{
    /// <summary>One-based position in extracted section order.</summary>
    public int Index { get; init; }
    /// <summary>Story/sheet/slide name.</summary>
    public string Name { get; init; } = "";
    /// <summary>Package-relative XML part retaining source identity.</summary>
    public string SourcePart { get; init; } = "";
    /// <summary>Content in source XML order, which may differ from visual reading order.</summary>
    public IReadOnlyList<OfficeContentBlock> Blocks { get; init; } = Array.Empty<OfficeContentBlock>();
    /// <summary>Nonempty block texts separated by newlines.</summary>
    public string FullText => string.Join("\n", Blocks.Select(b => b.Text).Where(t => t.Length != 0));
}

/// <summary>Office content extraction result with native and OCR origins retained.</summary>
public sealed record OfficeDocumentResult
{
    /// <summary>Format identified from package relationships and content types.</summary>
    public OfficeDocumentFormat Format { get; init; }
    /// <summary>Ordered source stories, worksheets or slides.</summary>
    public IReadOnlyList<OfficeDocumentSection> Sections { get; init; } = Array.Empty<OfficeDocumentSection>();
    /// <summary>Explicit unsupported-content and extraction limitations encountered.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>Nonempty section texts separated by blank lines.</summary>
    public string FullText => string.Join("\n\n", Sections.Select(s => s.FullText).Where(t => t.Length != 0));
    /// <summary>Serializes using source-generated metadata, including image OCR evidence.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, PaddleOcrJsonContext.Default.OfficeDocumentResult);
}
