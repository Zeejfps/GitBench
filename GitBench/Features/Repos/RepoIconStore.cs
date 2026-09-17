using System.Security.Cryptography;
using GitBench.Features.Diff;
using PngSharp.Api;

namespace GitBench.Features.Repos;

/// <summary>Imports sidebar artwork into the data directory, independent of its source file.</summary>
internal static class RepoIconStore
{
    internal const int MaxSize = 128;

    public static string? Import(string sourcePath, string directory)
    {
        string? staging = null;
        try
        {
            var source = RepoIconImage.Load(sourcePath);
            if (source is null) return null;
            var icon = Resize(source);
            var png = Png.EncodeToByteArray(Png.CreateRgba(icon.Width, icon.Height, icon.Rgba));
            // Immutable names invalidate sidebar image caches and share identical artwork.
            // Keep previous files: state.json is written asynchronously and may still reference them.
            var destination = Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(png)) + ".png");
            if (File.Exists(destination)) return destination;
            Directory.CreateDirectory(directory);
            staging = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(staging, png);
            File.Move(staging, destination, overwrite: true);
            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (staging is not null)
            {
                try { File.Delete(staging); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static ImageFrame Resize(ImageFrame source)
    {
        if (source.Width <= MaxSize && source.Height <= MaxSize) return source;
        var scale = (double)MaxSize / Math.Max(source.Width, source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var pixels = new byte[width * height * 4];
        var stepX = (double)source.Width / width;
        var stepY = (double)source.Height / height;

        // Area averaging avoids aliasing when shrinking large artwork. Weight RGB by alpha
        // so invisible pixels don't leave dark or colored fringes around transparent edges.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var left = x * stepX;
            var right = (x + 1) * stepX;
            var top = y * stepY;
            var bottom = (y + 1) * stepY;
            double red = 0, green = 0, blue = 0, alpha = 0;
            for (var sy = (int)top; sy < Math.Min(source.Height, (int)Math.Ceiling(bottom)); sy++)
            for (var sx = (int)left; sx < Math.Min(source.Width, (int)Math.Ceiling(right)); sx++)
            {
                var weight = (Math.Min(right, sx + 1) - Math.Max(left, sx))
                    * (Math.Min(bottom, sy + 1) - Math.Max(top, sy));
                var i = (sy * source.Width + sx) * 4;
                var a = source.Rgba[i + 3] * weight;
                alpha += a;
                red += source.Rgba[i] * a;
                green += source.Rgba[i + 1] * a;
                blue += source.Rgba[i + 2] * a;
            }
            var o = (y * width + x) * 4;
            if (alpha > 0)
            {
                pixels[o] = ToByte(red / alpha);
                pixels[o + 1] = ToByte(green / alpha);
                pixels[o + 2] = ToByte(blue / alpha);
            }
            pixels[o + 3] = ToByte(alpha / (stepX * stepY));
        }
        return new ImageFrame(width, height, pixels);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
