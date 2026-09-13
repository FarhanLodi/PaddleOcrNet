using EasyImageSharp;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure decode-path tests (no models, CI-safe) for <see cref="GuardedImageLoader"/>: EXIF orientation,
/// transparency flattening, single-frame decoding and the pixel guard.
/// </summary>
public class ServiceImageLoadingTests
{
    private static ImageLoadSettings Settings(bool exif = true, bool flatten = true, long maxPixels = 100_000_000)
        => new(maxPixels, exif, flatten);

    private static byte[] Encode(Image image, Action<Image, Stream> save)
    {
        using var ms = new MemoryStream();
        save(image, ms);
        return ms.ToArray();
    }

    [Fact]
    public void Exif_orientation_6_is_applied_after_decoding()
    {
        // Stored 80×40 with a dark block in the stored top-left; EXIF 6 means "rotate 90° clockwise to view".
        using var stored = new Image<Rgb24>(80, 40, new Rgb24(255, 255, 255));
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
                stored[x, y] = new Rgb24(0, 0, 0);
        stored.Metadata.ExifProfile = new ExifProfile();
        stored.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        var jpeg = Encode(stored, (img, s) => img.Save(s, new JpegEncoder { Quality = 95 }));

        using var upright = GuardedImageLoader.LoadFirstFrame(jpeg, Settings());

        Assert.Equal(40, upright.Width);
        Assert.Equal(80, upright.Height);
        // (x, y) → (H − y, x): the block lands in the viewed top-RIGHT corner.
        Assert.True(upright[35, 5].R < 60, "rotated block should be top-right");
        Assert.True(upright[5, 5].R > 200, "top-left should be background after rotation");

        using var raw = GuardedImageLoader.LoadFirstFrame(jpeg, Settings(exif: false));
        Assert.Equal(80, raw.Width);
        Assert.Equal(40, raw.Height);
    }

    [Fact]
    public void Dark_text_on_transparent_background_is_flattened_onto_white()
    {
        using var src = new Image<Rgba32>(60, 30, new Rgba32(0, 0, 0, 0));
        for (int y = 10; y < 20; y++)
            for (int x = 20; x < 40; x++)
                src[x, y] = new Rgba32(0, 0, 0, 255);
        var png = Encode(src, (img, s) => img.SaveAsPng(s));

        using var flat = GuardedImageLoader.LoadFirstFrame(png, Settings());
        Assert.Equal(new Rgb24(255, 255, 255), flat[2, 2]);
        Assert.Equal(new Rgb24(0, 0, 0), flat[30, 15]);

        // Without flattening the alpha is just dropped: everything is black (the bug being fixed).
        using var dropped = GuardedImageLoader.LoadFirstFrame(png, Settings(flatten: false));
        Assert.Equal(new Rgb24(0, 0, 0), dropped[2, 2]);
    }

    [Fact]
    public void Light_text_on_transparent_background_is_flattened_onto_black()
    {
        using var src = new Image<Rgba32>(60, 30, new Rgba32(0, 0, 0, 0));
        for (int y = 10; y < 20; y++)
            for (int x = 20; x < 40; x++)
                src[x, y] = new Rgba32(255, 255, 255, 255);
        var png = Encode(src, (img, s) => img.SaveAsPng(s));

        using var flat = GuardedImageLoader.LoadFirstFrame(png, Settings());
        Assert.Equal(new Rgb24(0, 0, 0), flat[2, 2]);
        Assert.Equal(new Rgb24(255, 255, 255), flat[30, 15]);
    }

    [Fact]
    public void Semi_transparent_pixels_are_alpha_blended()
    {
        using var src = new Image<Rgba32>(4, 1, new Rgba32(0, 0, 0, 255));
        src[0, 0] = new Rgba32(0, 0, 0, 128);

        using var flat = TransparencyFlattener.Flatten(src);
        // (0·128 + 255·127 + 127) / 255 = 127
        Assert.Equal(new Rgb24(127, 127, 127), flat[0, 0]);
        Assert.Equal(new Rgb24(0, 0, 0), flat[1, 0]);
    }

    [Fact]
    public void Opaque_png_decodes_identically_with_and_without_flattening()
    {
        using var src = new Image<Rgba32>(16, 16);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                src[x, y] = new Rgba32((byte)(x * 16), (byte)(y * 16), (byte)((x ^ y) * 16), 255);
        var png = Encode(src, (img, s) => img.SaveAsPng(s));

        using var flattened = GuardedImageLoader.LoadFirstFrame(png, Settings());
        using var direct = GuardedImageLoader.LoadFirstFrame(png, Settings(flatten: false));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(direct[x, y], flattened[x, y]);
    }

    [Fact]
    public void Single_image_load_decodes_only_the_first_frame()
    {
        var tiff = ServiceTestImages.ThreePageTiff();

        using var image = GuardedImageLoader.LoadFirstFrame(tiff, Settings());

        Assert.Single(image.Frames);
        Assert.Equal(ServiceTestImages.PageColors[0], image[3, 3]);
    }

    [Fact]
    public void Pixel_guard_rejects_before_decoding()
    {
        var tiff = ServiceTestImages.ThreePageTiff();
        Assert.Throws<ImageTooLargeException>(() => GuardedImageLoader.LoadFirstFrame(tiff, Settings(maxPixels: 10)));
        Assert.Throws<ImageTooLargeException>(() => GuardedImageLoader.LoadAllFrames(tiff, Settings(maxPixels: 10)));
    }
}
