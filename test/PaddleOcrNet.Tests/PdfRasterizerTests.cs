using Docnet.Core.Converters;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Pdf;
using PaddleOcrNet.Pdf.Internal;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure tests for the rasterizer helpers: the direct BGRA to Rgb24 conversion must be byte-identical to the former
/// NaiveTransparencyRemover + CloneAs path (so OCR input is unchanged), plus the auto-DPI rule and option validation.
/// </summary>
public class PdfRasterizerTests
{
    [Fact]
    public void Direct_bgra_conversion_matches_transparency_remover_then_clone_as()
    {
        const int width = 257, height = 131;
        var bgra = new byte[width * height * 4];
        new Random(1234).NextBytes(bgra);
        // Force the interesting alpha values into the mix.
        for (int i = 0; i < width * height; i++)
        {
            if (i % 5 == 0) bgra[i * 4 + 3] = 255;
            else if (i % 7 == 0) bgra[i * 4 + 3] = 0;
        }

        var legacyBytes = (byte[])bgra.Clone();
        new NaiveTransparencyRemover().Convert(legacyBytes);
        using var legacyBgra = Image.LoadPixelData<Bgra32>(legacyBytes, width, height);
        using var expected = legacyBgra.CloneAs<Rgb24>();

        using var actual = PdfRasterizer.ConvertBgraToRgb24(bgra, width, height);

        var expectedPixels = new Rgb24[width * height];
        var actualPixels = new Rgb24[width * height];
        expected.CopyPixelDataTo(expectedPixels);
        actual.CopyPixelDataTo(actualPixels);
        Assert.Equal(expectedPixels, actualPixels);
    }

    [Fact]
    public void Direct_conversion_rejects_a_short_buffer()
    {
        Assert.Throws<ArgumentException>(() => PdfRasterizer.ConvertBgraToRgb24(new byte[10], 2, 2));
    }

    [Theory]
    [InlineData(612, 792, 363)]        // US Letter: 4000 / 11 in
    [InlineData(595.27, 841.88, 342)]  // A4
    [InlineData(216, 360, 400)]        // 3 x 5 in card: clamped to the maximum
    [InlineData(2592, 1728, 150)]      // 36 x 24 in poster: clamped to the minimum
    public void Auto_dpi_targets_4000_pixels_on_the_longest_side_within_150_to_400(double widthPt, double heightPt, int expected)
    {
        Assert.Equal(expected, PdfRasterizer.ResolveAutoDpi(widthPt, heightPt));
    }

    [Fact]
    public void Dpi_zero_means_auto_and_other_out_of_range_values_are_rejected()
    {
        new PdfOcrOptions { Dpi = PdfOcrOptions.AutoDpi }.Validate();
        new PdfOcrOptions { Dpi = 36 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrOptions { Dpi = 20 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrOptions { Dpi = -1 }.Validate());
    }

    [Fact]
    public void Text_layer_defaults_to_ignore_and_undefined_values_are_rejected()
    {
        Assert.Equal(PdfTextLayerMode.Ignore, new PdfOcrOptions().TextLayer);
        Assert.Equal(200, new PdfOcrOptions().Dpi);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrOptions { TextLayer = (PdfTextLayerMode)42 }.Validate());
    }
}
