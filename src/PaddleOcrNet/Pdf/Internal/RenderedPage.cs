using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Pdf.Internal;

/// <summary>
/// One page produced by <see cref="PdfRasterizer.ReadPagesAsync"/>: its rendered image, and/or the lines read from
/// its embedded text layer. The consumer owns it and must dispose it.
/// </summary>
internal sealed class RenderedPage : IDisposable
{
    /// <summary>Original 1-based PDF page number.</summary>
    public required int PageNumber { get; init; }

    /// <summary>Total number of pages being processed.</summary>
    public required int PageCount { get; init; }

    /// <summary>The resolution the page was (or would have been) rendered at.</summary>
    public required int Dpi { get; init; }

    /// <summary>Page width in pixels at <see cref="Dpi"/>.</summary>
    public required int PixelWidth { get; init; }

    /// <summary>Page height in pixels at <see cref="Dpi"/>.</summary>
    public required int PixelHeight { get; init; }

    /// <summary>
    /// The rendered page, or <see langword="null"/> when the embedded text layer was used and no image was requested.
    /// </summary>
    public Image<Rgb24>? Image { get; init; }

    /// <summary>
    /// Lines from the embedded text layer when it was accepted for this page; <see langword="null"/> means OCR it.
    /// </summary>
    public IReadOnlyList<OcrLine>? EmbeddedLines { get; init; }

    /// <summary>Time spent reading and grouping the embedded characters.</summary>
    public TimeSpan EmbeddedTextDuration { get; init; }

    /// <inheritdoc />
    public void Dispose() => Image?.Dispose();
}
