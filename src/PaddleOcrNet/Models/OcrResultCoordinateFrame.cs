namespace PaddleOcrNet.Models;

/// <summary>The orientation in which OCR line and word coordinates are returned.</summary>
public enum OcrResultCoordinateFrame
{
    /// <summary>Coordinates on the decoded input image, before document-orientation correction.</summary>
    Original,

    /// <summary>Coordinates on the decoded input image rotated upright by the applied document rotation.</summary>
    Normalized,
}
