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
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Decodes a PNG or JPEG blob, or returns null for anything else (or a decode failure).</summary>
    public static ImagePreview? TryDecode(byte[] bytes)
    {
        try
        {
            if (IsPng(bytes)) return DecodePng(bytes);
            if (IsJpeg(bytes)) return DecodeJpeg(bytes);
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPng(byte[] b) => b.AsSpan().StartsWith(PngSignature);

    private static bool IsJpeg(byte[] b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

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
