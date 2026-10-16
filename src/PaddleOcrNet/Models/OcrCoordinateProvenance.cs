namespace PaddleOcrNet.Models;

/// <summary>The coordinate frame of OCR polygons.</summary>
public enum OcrCoordinateFrame
{
    /// <summary>Coordinates on the decoded input image; affine preprocessing has been mapped back.</summary>
    SourcePixels,
    /// <summary>Nonlinearly dewarped coordinates, with affine stages mapped back. Not original source positions.</summary>
    DewarpedPixels,
}

/// <summary>Applied geometry history. UVDoc emits pixels without an inverse field; no inverse is invented.</summary>
public sealed record OcrCoordinateProvenance
{
    /// <summary>Frame in which the returned polygons are expressed.</summary>
    public OcrCoordinateFrame Frame { get; init; }
    /// <summary>Whether returned coordinates identify original decoded-source positions.</summary>
    public bool SourceMappingAvailable { get; init; } = true;
    /// <summary>Applied clockwise document rotation, already mapped back in the output.</summary>
    public int DocumentRotationDegrees { get; init; }
    /// <summary>Applied deskew rotation, already mapped back in the output.</summary>
    public float DeskewDegrees { get; init; }
    /// <summary>Whether non-square pixels were rescaled and mapped back.</summary>
    public bool PixelAspectCorrected { get; init; }
    /// <summary>Whether a region-of-interest crop was requested.</summary>
    public bool RegionApplied { get; init; }
    /// <summary>Whether the nonlinear model actually produced a new image, not merely whether requested.</summary>
    public bool NonlinearDewarpApplied { get; init; }
}
