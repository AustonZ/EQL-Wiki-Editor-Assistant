using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Tests.Core.Ocr;

public class CapturedImageTests
{
    /// <summary>Builds a synthetic BGRA32 image where each pixel encodes its own (x, y) into (R, G), for
    /// verifying that Crop/Resize move the right pixels to the right places rather than just trusting sizes.</summary>
    private static CapturedImage MakeCoordinateImage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i + 0] = 0;              // B
                pixels[i + 1] = (byte)(y % 256); // G
                pixels[i + 2] = (byte)(x % 256); // R
                pixels[i + 3] = 255;             // A
            }
        }
        return new CapturedImage(width, height, pixels);
    }

    private static (byte R, byte G) PixelAt(CapturedImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return (image.Pixels[i + 2], image.Pixels[i + 1]);
    }

    [Fact]
    public void Constructor_RejectsMismatchedPixelBufferLength()
    {
        var ex = Assert.Throws<ArgumentException>(() => new CapturedImage(10, 10, new byte[10]));
        Assert.Contains("400", ex.Message); // 10*10*4
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(-1, 5)]
    public void Constructor_RejectsNonPositiveDimensions(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapturedImage(width, height, Array.Empty<byte>()));
    }

    [Fact]
    public void Crop_ExtractsCorrectRegion()
    {
        CapturedImage image = MakeCoordinateImage(20, 20);
        CapturedImage crop = image.Crop(new Rect(5, 7, 4, 3));

        Assert.Equal(4, crop.Width);
        Assert.Equal(3, crop.Height);
        // crop-local (0,0) should be the source's (5,7); crop-local (3,2) should be source's (8,9).
        Assert.Equal(PixelAt(image, 5, 7), PixelAt(crop, 0, 0));
        Assert.Equal(PixelAt(image, 8, 9), PixelAt(crop, 3, 2));
    }

    [Theory]
    [InlineData(-1, 0, 4, 4)]
    [InlineData(0, -1, 4, 4)]
    [InlineData(18, 0, 4, 4)] // right edge runs past width=20
    [InlineData(0, 18, 4, 4)] // bottom edge runs past height=20
    public void Crop_RejectsRegionsOutsideImage(int x, int y, int w, int h)
    {
        CapturedImage image = MakeCoordinateImage(20, 20);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Crop(new Rect(x, y, w, h)));
    }

    [Fact]
    public void Resize_ToSameDimensions_IsIdentity()
    {
        CapturedImage image = MakeCoordinateImage(16, 12);
        CapturedImage resized = image.Resize(16, 12);

        Assert.Equal(image.Pixels, resized.Pixels);
    }

    [Fact]
    public void Resize_Upscale_PreservesUniformColor()
    {
        var pixels = new byte[10 * 10 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = 10; pixels[i + 1] = 20; pixels[i + 2] = 30; pixels[i + 3] = 255;
        }
        var image = new CapturedImage(10, 10, pixels);

        CapturedImage upscaled = image.Resize(30, 30);

        Assert.Equal(30, upscaled.Width);
        Assert.Equal(30, upscaled.Height);
        for (int i = 0; i < upscaled.Pixels.Length; i += 4)
        {
            Assert.Equal(10, upscaled.Pixels[i + 0]);
            Assert.Equal(20, upscaled.Pixels[i + 1]);
            Assert.Equal(30, upscaled.Pixels[i + 2]);
        }
    }
}
