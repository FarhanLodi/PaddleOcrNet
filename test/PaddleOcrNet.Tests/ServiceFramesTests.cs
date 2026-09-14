using EasyImageSharp;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-free tests for the multi-frame API (<see cref="PaddleOcrFrameExtensions"/>) and the non-square
/// pixel correction (<see cref="NonSquarePixels"/>).
/// </summary>
public class ServiceFramesTests
{
    [Fact]
    public async Task Every_tiff_page_is_ocrd_in_order()
    {
        var service = new ServiceFakeOcrService
        {
            OnImage = img => ServiceFakeOcrService.Result($"{img[3, 3].R},{img[3, 3].G},{img[3, 3].B}", img.Width, img.Height),
        };
        using var stream = new MemoryStream(ServiceTestImages.ThreePageTiff());

        var results = new List<OcrFrameResult>();
        await foreach (var frame in service.ExtractTextFromImageFramesAsync(stream, OcrLanguage.English))
        {
            results.Add(frame);
        }

        Assert.Equal(new[] { 0, 1, 2 }, results.Select(r => r.FrameIndex));
        for (int i = 0; i < 3; i++)
        {
            var c = ServiceTestImages.PageColors[i];
            Assert.Equal($"{c.R},{c.G},{c.B}", results[i].Result.FullText);
            Assert.Equal(30, results[i].Result.SourceWidth);
        }
    }

    [Fact]
    public async Task Frames_path_overload_validates_the_file()
    {
        var service = new ServiceFakeOcrService();
        Assert.Throws<FileNotFoundException>(() =>
            service.ExtractTextFromImageFramesAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tif")));
        Assert.Throws<ArgumentException>(() => service.ExtractTextFromImageFramesAsync(" "));

        var path = Path.Combine(Path.GetTempPath(), $"paddleocr-frames-{Guid.NewGuid():N}.tif");
        await File.WriteAllBytesAsync(path, ServiceTestImages.ThreePageTiff());
        try
        {
            int count = 0;
            await foreach (var _ in service.ExtractTextFromImageFramesAsync(path, new[] { OcrLanguage.English })) count++;
            Assert.Equal(3, count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Frames_enumeration_honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var service = new ServiceFakeOcrService();
        using var stream = new MemoryStream(ServiceTestImages.ThreePageTiff());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.ExtractTextFromImageFramesAsync(stream, OcrLanguage.English, cancellationToken: cts.Token))
            {
                cts.Cancel();
            }
        });
    }

    [Fact]
    public void Fax_resolution_scales_the_low_resolution_axis_up()
    {
        Assert.True(NonSquarePixels.TryGetScale(204, 98, out double sx, out double sy));
        Assert.Equal(1.0, sx);
        Assert.Equal(204.0 / 98.0, sy, 6);

        Assert.False(NonSquarePixels.TryGetScale(300, 300, out _, out _));
        Assert.False(NonSquarePixels.TryGetScale(300, 280, out _, out _)); // within 15%
        Assert.False(NonSquarePixels.TryGetScale(0, 98, out _, out _));
    }

    [Fact]
    public void Square_pixel_copy_and_mapping_round_trip_to_the_original_grid()
    {
        using var fax = new Image<Rgb24>(204, 98, new Rgb24(255, 255, 255));
        fax.Metadata.SetResolution(204, 98, PixelResolutionUnit.PixelsPerInch);

        using var square = NonSquarePixels.CreateSquarePixelCopy(fax);
        Assert.NotNull(square);
        Assert.Equal(204, square!.Width);
        Assert.Equal(204, square.Height);

        // A box found at (10,40)-(110,80) on the 204×204 grid is y · 98/204 on the 204×98 grid; x is unchanged.
        var poly = new[] { new OcrPoint(10, 40), new OcrPoint(110, 40), new OcrPoint(110, 80), new OcrPoint(10, 80) };
        var line = new OcrLine { Text = "x", BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) };

        var mapped = NonSquarePixels.MapToSource(new[] { line }, (double)fax.Width / square.Width, (double)fax.Height / square.Height)[0];

        Assert.Equal(40 * 98.0 / 204.0, mapped.BoundingBox.MinY, 6);
        Assert.Equal(80 * 98.0 / 204.0, mapped.BoundingBox.MaxY, 6);
        Assert.Equal(10, mapped.BoundingBox.MinX, 6);
        Assert.Equal(110, mapped.BoundingPolygon[2].X, 6);
        Assert.Equal(80 * 98.0 / 204.0, mapped.BoundingPolygon[2].Y, 6);
    }

    [Fact]
    public void Square_images_and_pixel_regions_are_handled()
    {
        using var normal = new Image<Rgb24>(50, 50);
        Assert.Null(NonSquarePixels.CreateSquarePixelCopy(normal)); // default 96×96 DPI

        var pixels = NonSquarePixels.ScaleRegion(OcrRegion.Pixels(10, 10, 20, 20), 1, 2);
        Assert.Equal(20, pixels.Y);
        Assert.Equal(40, pixels.Height);

        var fraction = OcrRegion.Fraction(0, 0.5, 1, 0.5);
        Assert.Equal(fraction, NonSquarePixels.ScaleRegion(fraction, 1, 2));
    }

    [Fact]
    public void Preprocessing_defaults_correct_non_square_pixels()
    {
        Assert.True(PreprocessingOptions.None.CorrectNonSquarePixels);
        Assert.True(new PaddleOcrServiceOptions().ApplyExifOrientation);
        Assert.True(new PaddleOcrServiceOptions().FlattenTransparency);
    }
}
