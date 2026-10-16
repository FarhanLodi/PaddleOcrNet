namespace PaddleOcrNet.Models;

/// <summary>Sampled luminance diagnostics; not a learned degradation classifier or an accuracy estimate.</summary>
public readonly record struct PageImageQuality
{
    /// <summary>Number of sampled center pixels (at most 4096).</summary>
    public int SampleCount { get; init; }
    /// <summary>Luminance standard deviation divided by 255. Low values may describe blank or low-contrast pages.</summary>
    public double LuminanceContrast { get; init; }
    /// <summary>Mean absolute adjacent-pixel differences divided by 255; a blur-sensitive proxy, not a blur probability.</summary>
    public double EdgeEnergy { get; init; }
    /// <summary>Range of mean luminance across sampled quadrants divided by 255; content can also cause this variation.</summary>
    public double IlluminationVariation { get; init; }
}
