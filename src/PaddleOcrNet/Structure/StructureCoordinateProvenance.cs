namespace PaddleOcrNet.Structure;

/// <summary>Structure bounds use the analyzed page's pixels. Unlike OCR results, structure rotation is not mapped back.</summary>
public sealed record StructureCoordinateProvenance
{
    /// <summary>Width of the original decoded input before structure preprocessing.</summary>
    public int OriginalWidth { get; init; }
    /// <summary>Height of the original decoded input before structure preprocessing.</summary>
    public int OriginalHeight { get; init; }
    /// <summary>Clockwise rotation applied before analysis; bounds remain in the rotated frame.</summary>
    public int RotationApplied { get; init; }
    /// <summary>Whether the nonlinear model actually replaced the page.</summary>
    public bool NonlinearDewarpApplied { get; init; }
    /// <summary>True only when returned bounds already use original decoded-image coordinates.</summary>
    public bool IsOriginalFrame => RotationApplied == 0 && !NonlinearDewarpApplied;
    /// <summary>An affine inverse can be reconstructed from the original dimensions and rotation, but no nonlinear inverse is available.</summary>
    public bool InverseMappingAvailable => !NonlinearDewarpApplied;
    /// <summary>Declared frame of all block, line and cell geometry.</summary>
    public string Frame => IsOriginalFrame ? "source-pixels" : "structure-preprocessed-pixels";
}
