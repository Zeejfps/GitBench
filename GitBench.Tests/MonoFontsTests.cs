using GitBench.Controls;
using Xunit;
using ZGF.AppUtils;
using ZGF.Fonts;

namespace GitBench.Tests;

/// <summary>
/// The four monospaced faces are embedded, loadable, and share one metric. The last of those is
/// what a cell grid rests on: a bold run that advanced differently from a regular one would drift
/// out of its columns, and nothing but a test notices a face swapped for one from another release.
/// </summary>
public class MonoFontsTests
{
    private const int PixelSize = 16;

    private static readonly string[] Faces =
    [
        "JetBrainsMono-Regular.ttf",
        "JetBrainsMono-Bold.ttf",
        "JetBrainsMono-Italic.ttf",
        "JetBrainsMono-BoldItalic.ttf",
    ];

    public static TheoryData<string> FaceFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var face in Faces)
                data.Add(face);
            return data;
        }
    }

    private static byte[] Face(string file) =>
        EmbeddedAssets.LoadBytes(typeof(MonoFonts).Assembly, file);

    [Theory]
    [MemberData(nameof(FaceFiles))]
    public void EveryFace_IsEmbeddedAndLoads(string file)
    {
        using var fonts = new FreeTypeFontBackend();

        var handle = fonts.LoadFontFromMemory(Face(file), PixelSize);

        Assert.True(handle.IsValid);
        Assert.False(fonts.ResolveGlyph(handle, 'A').IsMissing);
    }

    [Fact]
    public void EveryFace_AdvancesTheSameWidth()
    {
        using var fonts = new FreeTypeFontBackend();

        var advances = Faces.ToDictionary(file => file, file => Advance(fonts, file));

        Assert.Single(advances.Values.Distinct());
        Assert.True(advances.Values.First() > 0f, $"no advance measured: {string.Join(", ", advances)}");
    }

    [Fact]
    public void EveryFace_IsTheSameHeight()
    {
        using var fonts = new FreeTypeFontBackend();

        var heights = Faces
            .Select(file => fonts.LoadFontFromMemory(Face(file), PixelSize))
            .Select(handle => fonts.GetMetrics(handle).LineHeight)
            .Distinct();

        Assert.Single(heights);
    }

    private static float Advance(FreeTypeFontBackend fonts, string file)
    {
        var handle = fonts.LoadFontFromMemory(Face(file), PixelSize);
        Assert.True(fonts.TryGetGlyph(fonts.ResolveGlyph(handle, 'W'), out var wide));
        Assert.True(fonts.TryGetGlyph(fonts.ResolveGlyph(handle, 'i'), out var narrow));

        // Same face, two glyphs of wildly different ink: a proportional face would answer twice.
        Assert.Equal(wide.XAdvance, narrow.XAdvance);
        return wide.XAdvance;
    }
}
