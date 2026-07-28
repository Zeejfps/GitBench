using System.Buffers.Binary;
using JpegSharp.Api;
using PngSharp.Api;
using PngSharp.Spec.Chunks.IHDR;

namespace GitBench.Features.Diff;

/// <summary>
/// An image blob decoded for display: straight-alpha RGBA8 with top-down rows, the shape
/// <c>ICanvas.CreateOrUpdateRgbaImage</c> uploads. <see cref="ContentHash"/> identifies the
/// source blob so the surface can key its texture on content rather than on file path.
/// </summary>
internal sealed record ImagePreview(int Width, int Height, byte[] Rgba, int SourceBytes, ulong ContentHash);

/// <summary>
/// Turns a raw image blob into an <see cref="ImagePreview"/>. Format comes from the file's magic
/// bytes, not its extension, so a mislabeled file or a Git LFS pointer standing in for the real
/// blob simply fails to decode and the diff falls back to its "binary file" placeholder.
/// </summary>
internal static class ImagePreviewDecoder
{
    /// <summary>Largest blob pulled out of git for a preview; bigger files keep the placeholder.</summary>
    public const int MaxSourceBytes = 24 * 1024 * 1024;

    // Decoding expands to 4 bytes per pixel and the result is uploaded as a texture, so the
    // pixel count — not the compressed size — is what bounds memory. 16 MP ≈ 64 MB of RGBA.
    private const long MaxPixels = 16_000_000;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int IcoDirectorySize = 6;
    private const int IcoEntrySize = 16;
    private const int BitmapInfoHeaderSize = 40;

    /// <summary>
    /// Whether a path is worth reading the blob for. Cheap extension test used to skip the git
    /// read entirely for the overwhelmingly common non-image binary; the real format check is
    /// the magic-byte sniff in <see cref="TryDecode"/>.
    /// </summary>
    public static bool IsPreviewablePath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Decodes a PNG, JPEG or ICO blob, or returns null for anything else (or a decode failure).</summary>
    public static ImagePreview? TryDecode(byte[] bytes)
    {
        try
        {
            if (IsPng(bytes)) return DecodePng(bytes);
            if (IsJpeg(bytes)) return DecodeJpeg(bytes);
            if (IsIco(bytes)) return DecodeIco(bytes);
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPng(byte[] b) => b.AsSpan().StartsWith(PngSignature);

    private static bool IsJpeg(byte[] b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    // An icon has no magic string, just a fixed directory header: reserved 0, then type 1.
    // Type 2 is a cursor, which shares the container but carries a hotspot instead of a colour
    // plane count and is not offered for preview.
    private static bool IsIco(byte[] b) =>
        b.Length >= IcoDirectorySize && b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0;

    private static ImagePreview? DecodeJpeg(byte[] bytes)
    {
        var info = Jpeg.Identify(bytes);
        if (!IsWithinPixelBudget(info.Width, info.Height)) return null;

        var image = Jpeg.Decode(bytes);
        var width = image.Width;
        var height = image.Height;
        // Rgba8888 packs a pixel as (R<<24)|(G<<16)|(B<<8)|A, so unpack by shift rather than
        // reinterpreting the int span — that would depend on the machine's endianness.
        var packed = new int[width * height];
        image.ToRgba8888(packed);

        var rgba = new byte[packed.Length * 4];
        for (var i = 0; i < packed.Length; i++)
        {
            var p = packed[i];
            var o = i * 4;
            rgba[o] = (byte)(p >> 24);
            rgba[o + 1] = (byte)(p >> 16);
            rgba[o + 2] = (byte)(p >> 8);
            rgba[o + 3] = (byte)p;
        }
        return new ImagePreview(width, height, rgba, bytes.Length, Fnv1A64(bytes));
    }

    /// <summary>
    /// Decodes the largest image in an icon container. An .ico is a ladder of separately drawn
    /// artwork, and a single-image preview can only show one of them; the largest is the one that
    /// carries the detail, so a change to it is what a reader is most likely looking for.
    /// </summary>
    private static ImagePreview? DecodeIco(byte[] bytes)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        if (count == 0 || bytes.Length < IcoDirectorySize + count * IcoEntrySize) return null;

        long bestPixels = -1;
        var bestDepth = -1;
        var bestOffset = 0;
        var bestLength = 0;

        for (var i = 0; i < count; i++)
        {
            var entry = bytes.AsSpan(IcoDirectorySize + i * IcoEntrySize, IcoEntrySize);
            // The dimension fields are single bytes, so 256 — the largest size an icon may hold —
            // is stored as 0.
            int width = entry[0] == 0 ? 256 : entry[0];
            int height = entry[1] == 0 ? 256 : entry[1];
            int depth = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (length <= 0 || offset < IcoDirectorySize || (long)offset + length > bytes.Length) continue;

            var pixels = (long)width * height;
            // Same size at a richer depth wins: a file carrying both a legacy palettized entry and
            // a 32bpp one lists them at equal dimensions.
            if (pixels < bestPixels || (pixels == bestPixels && depth <= bestDepth)) continue;
            bestPixels = pixels;
            bestDepth = depth;
            bestOffset = offset;
            bestLength = length;
        }

        if (bestPixels < 0) return null;

        var image = bytes.AsSpan(bestOffset, bestLength);
        // Vista onwards stores the large entries as an embedded PNG; everything else is a bare DIB.
        var decoded = image.StartsWith(PngSignature) ? DecodePng(image.ToArray()) : DecodeIconDib(image);

        // Report the whole container, not the one entry that got picked: the caption is describing
        // the file, and the hash has to change when any entry does or the texture goes stale.
        return decoded == null
            ? null
            : decoded with { SourceBytes = bytes.Length, ContentHash = Fnv1A64(bytes) };
    }

    /// <summary>
    /// Decodes one icon entry stored as a device-independent bitmap: a BITMAPINFOHEADER whose
    /// height covers the colour rows *and* a trailing 1bpp AND mask, bottom-up BGRA/BGR/indexed
    /// rows padded to 4 bytes.
    /// </summary>
    private static ImagePreview? DecodeIconDib(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < BitmapInfoHeaderSize) return null;
        // Icons in the wild only ever use the 40-byte header; the OS/2 and v4/v5 variants would
        // shift every offset below.
        if (BinaryPrimitives.ReadUInt32LittleEndian(dib) != BitmapInfoHeaderSize) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]) != 0) return null; // BI_RGB only

        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        // The stored height stacks the colour rows and the mask, so it is twice the real height.
        var height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]) / 2;
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        if (!IsWithinPixelBudget(width, height)) return null;

        var declaredColors = (int)BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        var paletteCount = bitCount <= 8 ? (declaredColors != 0 ? declaredColors : 1 << bitCount) : 0;
        var paletteBytes = paletteCount * 4;

        var colorStride = (width * bitCount + 31) / 32 * 4;
        var maskStride = (width + 31) / 32 * 4;
        var colorOffset = BitmapInfoHeaderSize + paletteBytes;
        var maskOffset = colorOffset + colorStride * height;
        if (colorOffset + (long)colorStride * height > dib.Length) return null;

        // A 32bpp entry sometimes ships without the mask. Anything shallower has nowhere else to
        // keep its transparency, so a missing mask there means the entry is malformed.
        var hasMask = maskOffset + (long)maskStride * height <= dib.Length;
        if (!hasMask && bitCount != 32) return null;

        var palette = dib.Slice(BitmapInfoHeaderSize, paletteBytes);
        var rgba = new byte[width * height * 4];
        var anyAlpha = false;

        for (var y = 0; y < height; y++)
        {
            var row = dib.Slice(colorOffset + (height - 1 - y) * colorStride, colorStride);
            var o = y * width * 4;
            for (var x = 0; x < width; x++, o += 4)
            {
                byte r, g, b, a = 255;
                switch (bitCount)
                {
                    case 32:
                        b = row[x * 4];
                        g = row[x * 4 + 1];
                        r = row[x * 4 + 2];
                        a = row[x * 4 + 3];
                        break;
                    case 24:
                        b = row[x * 3];
                        g = row[x * 3 + 1];
                        r = row[x * 3 + 2];
                        break;
                    case 8:
                    case 4:
                    case 1:
                        var index = PaletteIndex(row, x, bitCount);
                        if (index >= paletteCount) return null;
                        b = palette[index * 4];
                        g = palette[index * 4 + 1];
                        r = palette[index * 4 + 2];
                        break;
                    default:
                        return null;
                }

                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = a;
                anyAlpha |= a != 0;
            }
        }

        // Below 32bpp the transparency lives entirely in the mask. A 32bpp entry whose alpha is
        // uniformly zero is fully invisible as written, which no author intends — Windows treats
        // that as "no alpha channel" and falls back to the mask, so match it.
        if (bitCount != 32 || !anyAlpha)
        {
            if (hasMask) ApplyAndMask(rgba, dib, maskOffset, maskStride, width, height);
            else for (var o = 3; o < rgba.Length; o += 4) rgba[o] = 255;
        }

        return new ImagePreview(width, height, rgba, dib.Length, Fnv1A64(dib));
    }

    // A set bit means the colour pixel underneath it is transparent.
    private static void ApplyAndMask(
        byte[] rgba, ReadOnlySpan<byte> dib, int maskOffset, int maskStride, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var row = dib.Slice(maskOffset + (height - 1 - y) * maskStride, maskStride);
            var o = y * width * 4 + 3;
            for (var x = 0; x < width; x++, o += 4)
                rgba[o] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
        }
    }

    private static int PaletteIndex(ReadOnlySpan<byte> row, int x, int bitCount) => bitCount switch
    {
        8 => row[x],
        4 => (row[x >> 1] >> ((x & 1) == 0 ? 4 : 0)) & 0x0F,
        _ => (row[x >> 3] >> (7 - (x & 7))) & 1,
    };

    private static ImagePreview? DecodePng(byte[] bytes)
    {
        var png = Png.DecodeFromByteArray(bytes);
        var ihdr = png.Ihdr;
        var width = (int)ihdr.Width;
        var height = (int)ihdr.Height;
        if (!IsWithinPixelBudget(width, height)) return null;

        var src = png.PixelData;
        var stride = ihdr.GetBytesPerPixel();
        // PngSharp unpacks sub-byte depths to one byte per sample, so a pixel is always
        // `stride` bytes; at depth 16 each sample is 2 bytes, big-endian.
        var wide = ihdr.BitDepth == 16;
        var sampleStep = wide ? 2 : 1;
        // Sub-byte grayscale samples are raw 0..(2^depth-1) values that have to be stretched to
        // the full 8-bit range; palette indices must NOT be stretched.
        var grayMax = (1 << ihdr.BitDepth) - 1;

        var palette = png.Plte?.Entries;
        var paletteAlpha = ihdr.ColorType == ColorType.IndexedColor ? png.Trns?.Data : null;

        var rgba = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            var rowStart = y * width;
            for (var x = 0; x < width; x++)
            {
                var s = (rowStart + x) * stride;
                var o = (rowStart + x) * 4;
                byte r, g, b, a = 255;

                switch (ihdr.ColorType)
                {
                    case ColorType.Grayscale:
                        r = g = b = ScaleGray(src[s], ihdr.BitDepth, grayMax);
                        break;
                    case ColorType.GrayscaleWithAlpha:
                        r = g = b = src[s];
                        a = src[s + sampleStep];
                        break;
                    case ColorType.TrueColor:
                        r = src[s];
                        g = src[s + sampleStep];
                        b = src[s + sampleStep * 2];
                        break;
                    case ColorType.TrueColorWithAlpha:
                        r = src[s];
                        g = src[s + sampleStep];
                        b = src[s + sampleStep * 2];
                        a = src[s + sampleStep * 3];
                        break;
                    case ColorType.IndexedColor:
                        if (palette == null) return null;
                        var index = src[s];
                        var p = index * 3;
                        if (p + 2 >= palette.Length) return null;
                        r = palette[p];
                        g = palette[p + 1];
                        b = palette[p + 2];
                        if (paletteAlpha != null && index < paletteAlpha.Length) a = paletteAlpha[index];
                        break;
                    default:
                        return null;
                }

                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = a;
            }
        }

        return new ImagePreview(width, height, rgba, bytes.Length, Fnv1A64(bytes));
    }

    // A 16-bit or 8-bit sample is already the high byte of the value; narrower ones occupy the
    // low bits and have to be stretched so 1-bit black/white doesn't come out as 0 and 1.
    private static byte ScaleGray(byte sample, byte bitDepth, int max) =>
        bitDepth >= 8 ? sample : (byte)(sample * 255 / max);

    private static bool IsWithinPixelBudget(int width, int height) =>
        width > 0 && height > 0 && (long)width * height <= MaxPixels;

    private static ulong Fnv1A64(ReadOnlySpan<byte> data)
    {
        var hash = 14695981039346656037ul;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= 1099511628211ul;
        }
        return hash;
    }
}
