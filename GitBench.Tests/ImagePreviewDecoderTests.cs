using GitBench.Features.Diff;
using JpegSharp.Api;
using PngSharp.Api;
using PngSharp.Spec.Chunks.IHDR;
using PngSharp.Spec.Chunks.PLTE;
using PngSharp.Spec.Chunks.tRNS;
using Xunit;

namespace GitBench.Tests;

// The decoder hands raw bytes to the canvas as an RGBA8 texture, so a wrong channel order or a
// mis-strided read is invisible in a build and shows up as a garbled preview. These round-trip
// each PNG color type and a JPEG through the real codecs and assert the pixels land in R,G,B,A
// order, top-down.
public class ImagePreviewDecoderTests
{
    [Fact]
    public void DecodesTrueColorWithAlphaPng()
    {
        // Two pixels: opaque red, half-transparent blue.
        var png = Png.EncodeToByteArray(Png.CreateRgba(2, 1,
            [255, 0, 0, 255, 0, 0, 255, 128]));

        var preview = ImagePreviewDecoder.TryDecode(png);

        Assert.NotNull(preview);
        Assert.Equal(2, preview.Width);
        Assert.Equal(1, preview.Height);
        Assert.Equal<byte[]>([255, 0, 0, 255, 0, 0, 255, 128], preview.Rgba);
        Assert.Equal(png.Length, preview.SourceBytes);
    }

    [Fact]
    public void DecodesTrueColorPngAsOpaque()
    {
        var png = Png.EncodeToByteArray(Png.CreateRgb(2, 1, [10, 20, 30, 40, 50, 60]));

        var preview = ImagePreviewDecoder.TryDecode(png);

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([10, 20, 30, 255, 40, 50, 60, 255], preview.Rgba);
    }

    [Fact]
    public void DecodesGrayscalePng()
    {
        var png = Png.EncodeToByteArray(Png.CreateGrayscale(2, 1, [0, 200]));

        var preview = ImagePreviewDecoder.TryDecode(png);

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([0, 0, 0, 255, 200, 200, 200, 255], preview.Rgba);
    }

    [Fact]
    public void DecodesGrayscaleWithAlphaPng()
    {
        var png = Png.EncodeToByteArray(Png.CreateGrayscaleWithAlpha(2, 1, [90, 255, 90, 0]));

        var preview = ImagePreviewDecoder.TryDecode(png);

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([90, 90, 90, 255, 90, 90, 90, 0], preview.Rgba);
    }

    [Fact]
    public void DecodesIndexedPngThroughItsPalette()
    {
        // Two palette entries — green (fully transparent via tRNS) and red — indexed at 8 bits,
        // the shape most tool-generated icons take.
        var png = Png.EncodeToByteArray(Png.Builder()
            .WithIhdr(new IhdrChunkData
            {
                Width = 2,
                Height = 1,
                BitDepth = 8,
                ColorType = ColorType.IndexedColor,
                CompressionMethod = CompressionMethod.DeflateWithSlidingWindow,
                FilterMethod = FilterMethod.AdaptiveFiltering,
                InterlaceMethod = InterlaceMethod.None,
            })
            .WithPlte(new PlteChunkData { Entries = [0, 255, 0, 255, 0, 0] })
            .WithTrns(new TrnsChunkData { Data = [0, 255] })
            .WithPixelData([0, 1])
            .Build());

        var preview = ImagePreviewDecoder.TryDecode(png);

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([0, 255, 0, 0, 255, 0, 0, 255], preview.Rgba);
    }

    [Fact]
    public void DecodesRowsTopDown()
    {
        // Row 0 white, row 1 black — a flipped decode would swap them.
        var png = Png.EncodeToByteArray(Png.CreateRgb(1, 2, [255, 255, 255, 0, 0, 0]));

        var preview = ImagePreviewDecoder.TryDecode(png);

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([255, 255, 255, 255, 0, 0, 0, 255], preview.Rgba);
    }

    [Fact]
    public void DecodesJpeg()
    {
        // JPEG is lossy, so assert the channels land in the right order rather than exact values:
        // a saturated red must come back red-dominant, not blue-dominant.
        var jpeg = JpegImage.CreateRgb(8, 8, SolidRgb(8, 8, 220, 30, 30)).Encode();

        var preview = ImagePreviewDecoder.TryDecode(jpeg);

        Assert.NotNull(preview);
        Assert.Equal(8, preview.Width);
        Assert.Equal(8, preview.Height);
        Assert.Equal(8 * 8 * 4, preview.Rgba.Length);
        Assert.True(preview.Rgba[0] > 180, $"expected a red-dominant pixel, got R={preview.Rgba[0]}");
        Assert.True(preview.Rgba[1] < 80, $"expected a low green channel, got G={preview.Rgba[1]}");
        Assert.True(preview.Rgba[2] < 80, $"expected a low blue channel, got B={preview.Rgba[2]}");
        Assert.Equal(255, preview.Rgba[3]);
    }

    [Fact]
    public void RejectsNonImageBytes()
    {
        // What an LFS pointer looks like standing in for the real blob.
        var pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 12\n"u8.ToArray();

        Assert.Null(ImagePreviewDecoder.TryDecode(pointer));
        Assert.Null(ImagePreviewDecoder.TryDecode([]));
    }

    [Fact]
    public void RejectsTruncatedPng()
    {
        var png = Png.EncodeToByteArray(Png.CreateRgb(4, 4, new byte[4 * 4 * 3]));

        Assert.Null(ImagePreviewDecoder.TryDecode(png[..(png.Length / 2)]));
    }

    [Theory]
    [InlineData("logo.png", true)]
    [InlineData("photo.JPG", true)]
    [InlineData("photo.jpeg", true)]
    [InlineData("icon.svg", false)]
    [InlineData("bundle.wasm", false)]
    [InlineData("README", false)]
    public void RecognizesPreviewablePaths(string path, bool expected)
        => Assert.Equal(expected, ImagePreviewDecoder.IsPreviewablePath(path));

    private static byte[] SolidRgb(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 3] = r;
            pixels[i * 3 + 1] = g;
            pixels[i * 3 + 2] = b;
        }
        return pixels;
    }
}
