namespace PaddleOcrNet.Structure;

/// <summary>A confirmed table assembled as a separate view; all original page structures remain unchanged.</summary>
public sealed record DocumentTableView
{
    /// <summary>Serializes the joined view and original row membership without reflection.</summary>
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, StructureJsonContext.Unescaped.DocumentTableView);

    /// <summary>Original table blocks, in confirmed continuation order.</summary>
    public required IReadOnlyList<DocumentBlockReference> Sources { get; init; }
    /// <summary>Number of logical columns, including columns covered by merged cells.</summary>
    public required int ColumnCount { get; init; }
    /// <summary>Output rows; each row records its original page, table and row index.</summary>
    public required IReadOnlyList<DocumentTableRow> Rows { get; init; }
    /// <summary>Repeated first rows omitted from the view, with their complete source evidence.</summary>
    public required IReadOnlyList<DocumentTableRow> OmittedHeaders { get; init; }
}

/// <summary>One row in a confirmed table view, retaining original membership.</summary>
public sealed record DocumentTableRow
{
    /// <summary>Original page and table block index.</summary>
    public required DocumentBlockReference Source { get; init; }
    /// <summary>Zero-based row index in the original table.</summary>
    public required int SourceRowIndex { get; init; }
    /// <summary>Cells originating in this row; continuation positions of rowspans have no duplicate cell.</summary>
    public required IReadOnlyList<DocumentTableCell> Cells { get; init; }
}

/// <summary>An original table cell with its logical column and unchanged spans.</summary>
public sealed record DocumentTableCell
{
    /// <summary>Zero-based logical column index.</summary>
    public required int ColumnIndex { get; init; }
    /// <summary>Decoded cell text; original markup remains in the source structure.</summary>
    public required string Text { get; init; }
    /// <summary>Number of logical columns covered by this cell.</summary>
    public int ColumnSpan { get; init; } = 1;
    /// <summary>Number of rows covered within the original table.</summary>
    public int RowSpan { get; init; } = 1;
}
