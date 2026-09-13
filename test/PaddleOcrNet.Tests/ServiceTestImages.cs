using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Synthetic images shared by the Service* tests.
/// </summary>
internal static class ServiceTestImages
{
    internal static readonly Rgb24[] PageColors =
    {
        new(200, 30, 30), new(30, 200, 30), new(30, 30, 200),
    };

    /// <summary>A 3-page 30×20 TIFF whose pages are filled with <see cref="PageColors"/>.</summary>
    internal static byte[] ThreePageTiff()
    {
        using var image = new Image<Rgb24>(30, 20, PageColors[0]);
        for (int i = 1; i < PageColors.Length; i++)
        {
            image.Frames.AddFrame(Enumerable.Repeat(PageColors[i], 30 * 20).ToArray());
        }
        using var ms = new MemoryStream();
        image.SaveAsTiff(ms);
        return ms.ToArray();
    }
}
