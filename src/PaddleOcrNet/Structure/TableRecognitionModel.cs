namespace PaddleOcrNet.Structure;

/// <summary>
/// Selects which table-structure model the structure pipeline uses for <see cref="StructureBlockType.Table"/>
/// regions. Both produce HTML <c>&lt;table&gt;</c> markup with the region's OCR text matched into the cells.
/// </summary>
public enum TableRecognitionModel
{
    /// <summary>
    /// SLANet_plus — a single end-to-end structure model (488×488). The default, and the recommended
    /// choice: lightest and validated, it recovers the table grid and per-cell boxes in one pass. On the
    /// bundled fixtures it places every value in the right cell.
    /// </summary>
    SlanetPlus,

    /// <summary>
    /// SLANeXt (PP-StructureV3 "table recognition v2"). A lightweight table-type classifier
    /// (<c>PP-LCNet_x1_0_table_cls</c>) first decides whether the table is <i>wired</i> (ruled/bordered) or
    /// <i>wireless</i> (borderless), then the matching SLANeXt structure model (512×512) recovers the grid.
    /// Downloads three extra models on first use.
    /// <para>
    /// <b>Known issue — prefer <see cref="SlanetPlus"/>.</b> The cell boxes this path decodes come out far
    /// too tall and a large share of them clamp to the bottom edge of the crop, so OCR lines are matched into
    /// the wrong cells: on the bundled 16×6 fixture the mean cell is ~2.5× the true row height and 47 of 96
    /// cells clamp, which scrambles the recovered text even though the row/column <i>structure</i> decodes
    /// correctly. The location head evidently does not use the same coordinate convention as SLANet_plus,
    /// which this recognizer's rescale assumes. Until that is corrected, <see cref="SlanetPlus"/> is both the
    /// default and the accurate option.
    /// </para>
    /// </summary>
    SlaNeXt,
}
