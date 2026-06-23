using System;
using System.IO;
using GitBench.Platform;
using Xunit;
using ZGF.Fonts;

namespace GitBench.Tests;

public class FontFallbackTests
{
    [Fact]
    public void CjkFallbackResolverFindsExistingFontsOnMacOs()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var specs = SystemFonts.CjkFallbacks();
        Assert.NotEmpty(specs);
        Assert.All(specs, s => Assert.True(File.Exists(s.Path), $"resolved font missing: {s.Path}"));
    }


    private const string Latin = "/System/Library/Fonts/Helvetica.ttc";
    private const string Hiragino = "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc";

    private static bool FontsAvailable =>
        OperatingSystem.IsMacOS() && File.Exists(Latin) && File.Exists(Hiragino);

    [Fact]
    public void FallbackSuppliesGlyphsForCodePointsMissingFromPrimary()
    {
        if (!FontsAvailable)
            return; // system fonts unavailable; the macOS run covers this

        var fonts = new FreeTypeFontBackend();
        try
        {
            var primary = fonts.LoadFontFromFile(Latin, 16);
            var fallback = fonts.LoadFontFromFile(Hiragino, 16);
            fonts.RegisterFallbackFont(fallback);

            // 'A' is Latin (primary covers it); 'あ' is hiragana (only the fallback covers it).
            Span<ShapedGlyph> glyphs = stackalloc ShapedGlyph[8];
            var n = fonts.ShapeText(primary, "Aあ", glyphs);

            Assert.Equal(2, n);
            Assert.Equal(primary.Id, glyphs[0].FontId);
            Assert.Equal(fallback.Id, glyphs[1].FontId);
            Assert.NotEqual(0u, glyphs[0].GlyphIndex);
            Assert.NotEqual(0u, glyphs[1].GlyphIndex); // would be .notdef without the fallback
        }
        finally { fonts.Dispose(); }
    }

    [Fact]
    public void WithoutFallbackMissingGlyphsStayNotdef()
    {
        if (!FontsAvailable)
            return;

        var fonts = new FreeTypeFontBackend();
        try
        {
            var primary = fonts.LoadFontFromFile(Latin, 16);

            Span<ShapedGlyph> glyphs = stackalloc ShapedGlyph[8];
            var n = fonts.ShapeText(primary, "Aあ", glyphs);

            Assert.Equal(2, n);
            Assert.Equal(primary.Id, glyphs[1].FontId);
            Assert.Equal(0u, glyphs[1].GlyphIndex); // hiragana has no glyph in a Latin font
        }
        finally { fonts.Dispose(); }
    }
}
