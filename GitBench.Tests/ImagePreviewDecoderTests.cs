using System.Buffers.Binary;
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
        Assert.Equal<byte[]>([255, 0, 0, 255, 0, 0, 255, 128], preview.Primary.Rgba);
        Assert.Equal(png.Length, preview.SourceBytes);
    }

    [Fact]
    public void DecodesTrueColorPngAsOpaque()
    {
        var png = Png.EncodeToByteArray(Png.CreateRgb(2, 1, [10, 20, 30, 40, 50, 60]));

        var preview = ImagePreviewDecoder.TryDecode(png);

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([10, 20, 30, 255, 40, 50, 60, 255], preview.Primary.Rgba);
    }

    [Fact]
    public void DecodesGrayscalePng()
    {
        var png = Png.EncodeToByteArray(Png.CreateGrayscale(2, 1, [0, 200]));

        var preview = ImagePreviewDecoder.TryDecode(png);

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([0, 0, 0, 255, 200, 200, 200, 255], preview.Primary.Rgba);
    }

    [Fact]
    public void DecodesGrayscaleWithAlphaPng()
    {
        var png = Png.EncodeToByteArray(Png.CreateGrayscaleWithAlpha(2, 1, [90, 255, 90, 0]));

        var preview = ImagePreviewDecoder.TryDecode(png);

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([90, 90, 90, 255, 90, 90, 90, 0], preview.Primary.Rgba);
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
        Assert.Equal<byte[]>([0, 255, 0, 0, 255, 0, 0, 255], preview.Primary.Rgba);
    }

    [Fact]
    public void DecodesRowsTopDown()
    {
        // Row 0 white, row 1 black — a flipped decode would swap them.
        var png = Png.EncodeToByteArray(Png.CreateRgb(1, 2, [255, 255, 255, 0, 0, 0]));

        var preview = ImagePreviewDecoder.TryDecode(png);

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([255, 255, 255, 255, 0, 0, 0, 255], preview.Primary.Rgba);
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
        Assert.Equal(8 * 8 * 4, preview.Primary.Rgba.Length);
        Assert.True(preview.Primary.Rgba[0] > 180, $"expected a red-dominant pixel, got R={preview.Primary.Rgba[0]}");
        Assert.True(preview.Primary.Rgba[1] < 80, $"expected a low green channel, got G={preview.Primary.Rgba[1]}");
        Assert.True(preview.Primary.Rgba[2] < 80, $"expected a low blue channel, got B={preview.Primary.Rgba[2]}");
        Assert.Equal(255, preview.Primary.Rgba[3]);
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

    [Fact]
    public void DecodesEveryIcoEntryLargestFirst()
    {
        // A one-pixel entry alongside a two-pixel one: both come back, the larger leading whatever
        // order the file lists them in, and each entry's stored BGRA has to arrive as RGBA.
        var small = Dib32(1, 1, [0, 0, 255, 255]);
        var large = Dib32(2, 1, [255, 0, 0, 255, 0, 255, 0, 255]);
        var ico = BuildIco(new IcoEntry(1, 1, 32, small), new IcoEntry(2, 1, 32, large));

        var preview = ImagePreviewDecoder.TryDecode(ico);

        Assert.NotNull(preview);
        Assert.Equal(2, preview.Frames.Count);
        Assert.Equal(2, preview.Width);
        Assert.Equal(1, preview.Height);
        Assert.Equal<byte[]>([0, 0, 255, 255, 0, 255, 0, 255], preview.Primary.Rgba);
        Assert.Equal(1, preview.Frames[1].Width);
        Assert.Equal<byte[]>([255, 0, 0, 255], preview.Frames[1].Rgba);
        Assert.Equal(ico.Length, preview.SourceBytes);
    }

    [Fact]
    public void OrdersEqualSizedIcoEntriesByDepth()
    {
        // The pair a legacy Windows icon carries: one size drawn twice, palettized and 32bpp. The
        // richer one leads, and both keep the depth that is all that tells them apart on screen.
        var palettized = Dib8(1, 1, [0, 0, 255, 0], [0, 0, 0, 0], new byte[4]);
        var full = Dib32(1, 1, [0, 255, 0, 255]);
        var ico = BuildIco(new IcoEntry(1, 1, 8, palettized), new IcoEntry(1, 1, 32, full));

        var preview = ImagePreviewDecoder.TryDecode(ico);

        Assert.NotNull(preview);
        Assert.Equal(new int?[] { 32, 8 }, preview.Frames.Select(f => f.BitDepth));
        Assert.Equal<byte[]>([0, 255, 0, 255], preview.Primary.Rgba);
        Assert.Equal<byte[]>([255, 0, 0, 255], preview.Frames[1].Rgba);
    }

    [Fact]
    public void KeepsTheDecodableIcoEntriesWhenOneIsMalformed()
    {
        // A container is a ladder of independent drawings — a bad rung loses that rung, not the file.
        var ico = BuildIco(
            new IcoEntry(1, 1, 32, Dib32(1, 1, [0, 0, 255, 255])),
            new IcoEntry(2, 1, 32, [1, 2, 3, 4]));

        var preview = ImagePreviewDecoder.TryDecode(ico);

        Assert.NotNull(preview);
        Assert.Single(preview.Frames);
        Assert.Equal(1, preview.Width);
    }

    [Fact]
    public void GivesAPlainImageASingleUnlabelledFrame()
    {
        var png = Png.EncodeToByteArray(Png.CreateRgb(2, 1, [10, 20, 30, 40, 50, 60]));

        var preview = ImagePreviewDecoder.TryDecode(png);

        Assert.NotNull(preview);
        Assert.Single(preview.Frames);
        Assert.Null(preview.Primary.BitDepth);
    }

    [Fact]
    public void DecodesIcoRowsTopDown()
    {
        // Row 0 white, row 1 black. A DIB stores them bottom-up, so a missed flip swaps them.
        var dib = Dib32(1, 2, [255, 255, 255, 255, 0, 0, 0, 255]);

        var preview = ImagePreviewDecoder.TryDecode(BuildIco(new IcoEntry(1, 2, 32, dib)));

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([255, 255, 255, 255, 0, 0, 0, 255], preview.Primary.Rgba);
    }

    [Fact]
    public void KeepsIco32BppAlphaOverTheMask()
    {
        // Half-transparent red under an all-opaque mask: the alpha channel wins.
        var dib = Dib32(1, 1, [0, 0, 255, 128]);

        var preview = ImagePreviewDecoder.TryDecode(BuildIco(new IcoEntry(1, 1, 32, dib)));

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([255, 0, 0, 128], preview.Primary.Rgba);
    }

    [Fact]
    public void FallsBackToAndMaskWhenIco32BppAlphaIsBlank()
    {
        // Red and green written with a zero alpha channel — as written the entry is invisible, so
        // the mask is what actually carries the transparency. It marks only the second pixel.
        var mask = new byte[4];
        mask[0] = 0b0100_0000;
        var dib = Dib32(2, 1, [0, 0, 255, 0, 0, 255, 0, 0], mask);

        var preview = ImagePreviewDecoder.TryDecode(BuildIco(new IcoEntry(2, 1, 32, dib)));

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([255, 0, 0, 255, 0, 255, 0, 0], preview.Primary.Rgba);
    }

    [Fact]
    public void DecodesPalettizedIcoEntry()
    {
        // Palette index 0 red, 1 green (stored as BGRA quads); the mask hides the second pixel.
        var mask = new byte[4];
        mask[0] = 0b0100_0000;
        var dib = Dib8(2, 1, [0, 0, 255, 0, 0, 255, 0, 0], [0, 1, 0, 0], mask);

        var preview = ImagePreviewDecoder.TryDecode(BuildIco(new IcoEntry(2, 1, 8, dib)));

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([255, 0, 0, 255, 0, 255, 0, 0], preview.Primary.Rgba);
    }

    [Fact]
    public void DecodesEmbeddedPngIcoEntry()
    {
        // How every icon stores its large sizes. The reported weight must be the whole container,
        // not the length of the entry that happened to win.
        var png = Png.EncodeToByteArray(Png.CreateRgba(2, 1, [255, 0, 0, 255, 0, 0, 255, 128]));
        var ico = BuildIco(new IcoEntry(2, 1, 32, png));

        var preview = ImagePreviewDecoder.TryDecode(ico);

        Assert.NotNull(preview);
        Assert.Equal<byte[]>([255, 0, 0, 255, 0, 0, 255, 128], preview.Primary.Rgba);
        Assert.Equal(ico.Length, preview.SourceBytes);
    }

    [Fact]
    public void RejectsIcoWhoseEntryRunsPastEndOfFile()
    {
        var ico = BuildIco(new IcoEntry(1, 1, 32, Dib32(1, 1, [0, 0, 255, 255])));
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6 + 8), 0xFFFF);

        Assert.Null(ImagePreviewDecoder.TryDecode(ico));
    }

    [Fact]
    public void RejectsCursorContainer()
    {
        // Same layout as an icon but type 2, where the plane/depth fields are a hotspot instead.
        var cur = BuildIco(new IcoEntry(1, 1, 32, Dib32(1, 1, [0, 0, 255, 255])));
        cur[2] = 2;

        Assert.Null(ImagePreviewDecoder.TryDecode(cur));
    }

    [Theory]
    [InlineData("logo.png", true)]
    [InlineData("photo.JPG", true)]
    [InlineData("photo.jpeg", true)]
    [InlineData("app_icon.ico", true)]
    [InlineData("app_icon.ICO", true)]
    [InlineData("pointer.cur", false)]
    [InlineData("icon.svg", false)]
    [InlineData("bundle.wasm", false)]
    [InlineData("README", false)]
    public void RecognizesPreviewablePaths(string path, bool expected)
        => Assert.Equal(expected, ImagePreviewDecoder.IsPreviewablePath(path));

    private readonly record struct IcoEntry(int Width, int Height, int BitCount, byte[] Payload);

    private static byte[] BuildIco(params IcoEntry[] entries)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)entries.Length);

        var offset = 6 + 16 * entries.Length;
        foreach (var e in entries)
        {
            w.Write((byte)(e.Width == 256 ? 0 : e.Width));
            w.Write((byte)(e.Height == 256 ? 0 : e.Height));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)e.BitCount);
            w.Write((uint)e.Payload.Length);
            w.Write((uint)offset);
            offset += e.Payload.Length;
        }

        foreach (var e in entries) w.Write(e.Payload);
        return ms.ToArray();
    }

    // Pixels go in top-down for readability; the DIB itself is written bottom-up, height doubled,
    // with the AND mask trailing the colour rows — the shape a real icon entry takes.
    private static byte[] Dib32(int width, int height, byte[] bgraTopDown, byte[]? maskTopDown = null)
    {
        var stride = width * 4;
        var maskStride = (width + 31) / 32 * 4;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        WriteDibHeader(w, width, height, 32, 0);
        for (var y = height - 1; y >= 0; y--) w.Write(bgraTopDown, y * stride, stride);
        for (var y = height - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            if (maskTopDown != null) Array.Copy(maskTopDown, y * maskStride, row, 0, maskStride);
            w.Write(row);
        }
        return ms.ToArray();
    }

    private static byte[] Dib8(int width, int height, byte[] palette, byte[] indicesTopDown, byte[] maskTopDown)
    {
        var stride = (width * 8 + 31) / 32 * 4;
        var maskStride = (width + 31) / 32 * 4;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        WriteDibHeader(w, width, height, 8, palette.Length / 4);
        w.Write(palette);
        for (var y = height - 1; y >= 0; y--) w.Write(indicesTopDown, y * stride, stride);
        for (var y = height - 1; y >= 0; y--) w.Write(maskTopDown, y * maskStride, maskStride);
        return ms.ToArray();
    }

    private static void WriteDibHeader(BinaryWriter w, int width, int height, int bitCount, int paletteCount)
    {
        w.Write(40);
        w.Write(width);
        w.Write(height * 2);
        w.Write((ushort)1);
        w.Write((ushort)bitCount);
        w.Write(0); // BI_RGB
        w.Write(0); // biSizeImage
        w.Write(0); // biXPelsPerMeter
        w.Write(0); // biYPelsPerMeter
        w.Write(paletteCount);
        w.Write(0); // biClrImportant
    }

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
