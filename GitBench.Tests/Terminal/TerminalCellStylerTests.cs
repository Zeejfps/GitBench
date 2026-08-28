using System.Text;
using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using GitBench.Theming;
using Xunit;

namespace GitBench.Tests;

// The engine leaves a cell's colour unresolved on purpose — Default and Indexed follow the user's
// theme, a literal Rgb must not — so this suite pins the one place that turns the three cases into
// pixels: the xterm 16/cube/greyscale arithmetic, where the theme's own two defaults land, that Rgb
// is never snapped to a palette slot, that every result is opaque, and the exact order in which
// inverse, dim and hidden are spent. Bold is a face here, never a brighter colour.
public class TerminalCellStylerTests
{
    // Slot n is 0xFF51000n so a failure names the ANSI slot it landed on. None of these can arise
    // from the 6x6x6 cube (channels come from {00,5F,87,AF,D7,FF}) or the greyscale ramp (equal
    // channels), so an index resolved through the wrong range cannot pass by coincidence.
    private static readonly AnsiColors TestAnsi = new(
        Black: 0xFF510000u,
        Red: 0xFF510001u,
        Green: 0xFF510002u,
        Yellow: 0xFF510003u,
        Blue: 0xFF510004u,
        Magenta: 0xFF510005u,
        Cyan: 0xFF510006u,
        White: 0xFF510007u,
        BrightBlack: 0xFF510008u,
        BrightRed: 0xFF510009u,
        BrightGreen: 0xFF51000Au,
        BrightYellow: 0xFF51000Bu,
        BrightBlue: 0xFF51000Cu,
        BrightMagenta: 0xFF51000Du,
        BrightCyan: 0xFF51000Eu,
        BrightWhite: 0xFF51000Fu);

    private const uint ThemeForeground = 0xFFC0FFEEu;
    private const uint ThemeBackground = 0xFF102030u;

    private static readonly TerminalStyles TestStyles = new(
        Ansi: TestAnsi,
        DefaultForeground: ThemeForeground,
        DefaultBackground: ThemeBackground,
        Cursor: 0xFFFACADEu);

    // Same colours with the alpha channel deliberately wrong, in every slot the styler reads. The
    // bright slot carries a plausible translucent value because that is where a real theme is most
    // likely to source one (dark's TextDisabled is 0x80B5B9C0).
    private static readonly TerminalStyles TranslucentStyles = TestStyles with
    {
        Ansi = TestAnsi with { Red = 0x00510001u, BrightWhite = 0x8051000Fu },
        DefaultForeground = 0x40C0FFEEu,
        DefaultBackground = 0x80102030u,
    };

    // Two literal colours whose midpoints toward ThemeBackground land on whole numbers, so the
    // ordering tests below turn on the order alone and not on how a half rounds.
    private static readonly TerminalColor LiteralFore = TerminalColor.Rgb(200, 100, 50);
    private static readonly TerminalColor LiteralBack = TerminalColor.Rgb(20, 220, 120);
    private const uint LiteralForeArgb = 0xFFC86432u;
    private const uint LiteralBackArgb = 0xFF14DC78u;
    private const uint LiteralForeDimmed = 0xFF6C4231u;
    // The two literals mixed with each other; symmetric, so it serves either direction.
    private const uint LiteralPairMidpoint = 0xFF6EA055u;

    private static ICellStyler Styler(TerminalStyles? styles = null) =>
        new TerminalCellStyler(styles ?? TestStyles);

    private static TerminalCell Cell(
        TerminalColor foreground,
        TerminalColor background,
        CellAttributes attributes = CellAttributes.None) =>
        new(new Rune('A'), foreground, background, attributes, CellWidth.Single);

    // All sixteen, because the styler's switch is the only place a slot transposition can live: a
    // swapped Blue/Magenta or BrightCyan/BrightMagenta is invisible to any sampled subset.
    [Theory]
    [InlineData(0, 0xFF510000u)]
    [InlineData(1, 0xFF510001u)]
    [InlineData(2, 0xFF510002u)]
    [InlineData(3, 0xFF510003u)]
    [InlineData(4, 0xFF510004u)]
    [InlineData(5, 0xFF510005u)]
    [InlineData(6, 0xFF510006u)]
    [InlineData(7, 0xFF510007u)]
    [InlineData(8, 0xFF510008u)]
    [InlineData(9, 0xFF510009u)]
    [InlineData(10, 0xFF51000Au)]
    [InlineData(11, 0xFF51000Bu)]
    [InlineData(12, 0xFF51000Cu)]
    [InlineData(13, 0xFF51000Du)]
    [InlineData(14, 0xFF51000Eu)]
    [InlineData(15, 0xFF51000Fu)]
    public void IndexedBelowSixteenResolvesToTheThemeAnsiSlot(int index, uint expected)
    {
        var styler = Styler();
        var asForeground = styler.Style(Cell(TerminalColor.Indexed((byte)index), TerminalColor.Default));
        var asBackground = styler.Style(Cell(TerminalColor.Default, TerminalColor.Indexed((byte)index)));

        Assert.Equal(expected, asForeground.Foreground);
        Assert.Equal(expected, asBackground.Background);
    }

    [Theory]
    [InlineData(16, 0xFF000000u)]
    [InlineData(17, 0xFF00005Fu)]
    [InlineData(21, 0xFF0000FFu)]
    [InlineData(46, 0xFF00FF00u)]
    [InlineData(137, 0xFFAF875Fu)]
    [InlineData(196, 0xFFFF0000u)]
    [InlineData(230, 0xFFFFFFD7u)]
    [InlineData(231, 0xFFFFFFFFu)]
    public void IndexedInCubeRangeResolvesToTheColourCube(int index, uint expected)
    {
        var styler = Styler();
        var asForeground = styler.Style(Cell(TerminalColor.Indexed((byte)index), TerminalColor.Default));
        var asBackground = styler.Style(Cell(TerminalColor.Default, TerminalColor.Indexed((byte)index)));

        Assert.Equal(expected, asForeground.Foreground);
        Assert.Equal(expected, asBackground.Background);
    }

    [Theory]
    [InlineData(232, 0xFF080808u)]
    [InlineData(233, 0xFF121212u)]
    [InlineData(243, 0xFF767676u)]
    [InlineData(254, 0xFFE4E4E4u)]
    [InlineData(255, 0xFFEEEEEEu)]
    public void IndexedAboveCubeResolvesToTheGreyscaleRamp(int index, uint expected)
    {
        var styler = Styler();
        var asForeground = styler.Style(Cell(TerminalColor.Indexed((byte)index), TerminalColor.Default));
        var asBackground = styler.Style(Cell(TerminalColor.Default, TerminalColor.Indexed((byte)index)));

        Assert.Equal(expected, asForeground.Foreground);
        Assert.Equal(expected, asBackground.Background);
    }

    [Fact]
    public void DefaultTakesTheThemeForegroundAndBackgroundInTheirOwnRoles()
    {
        var style = Styler().Style(Cell(TerminalColor.Default, TerminalColor.Default));

        Assert.Equal(ThemeForeground, style.Foreground);
        Assert.Equal(ThemeBackground, style.Background);
    }

    [Theory]
    [InlineData(0, 0, 0, 0xFF000000u)]
    [InlineData(255, 255, 255, 0xFFFFFFFFu)]
    [InlineData(18, 52, 86, 0xFF123456u)]
    public void RgbIsPackedLiterally(int red, int green, int blue, uint expected)
    {
        var styler = Styler();
        var color = TerminalColor.Rgb((byte)red, (byte)green, (byte)blue);

        Assert.Equal(expected, styler.Style(Cell(color, TerminalColor.Default)).Foreground);
        Assert.Equal(expected, styler.Style(Cell(TerminalColor.Default, color)).Background);
    }

    [Theory]
    [InlineData(0, 0, 254, 0xFF0000FEu)] // one off the cube's #0000FF
    [InlineData(1, 1, 1, 0xFF010101u)] // just under the greyscale ramp's first step
    [InlineData(0x51, 0x00, 0x01, 0xFF510001u)] // exactly the theme's ANSI slot 1
    [InlineData(95, 135, 175, 0xFF5F87AFu)] // every channel a legal cube component
    public void RgbIsNeverSnappedToAPaletteEntry(int red, int green, int blue, uint expected)
    {
        var style = Styler().Style(Cell(TerminalColor.Rgb((byte)red, (byte)green, (byte)blue), TerminalColor.Default));

        Assert.Equal(expected, style.Foreground);
    }

    [Fact]
    public void ThemeColoursAreForcedOpaque()
    {
        var styler = Styler(TranslucentStyles);

        var defaults = styler.Style(Cell(TerminalColor.Default, TerminalColor.Default));
        Assert.Equal(ThemeForeground, defaults.Foreground);
        Assert.Equal(ThemeBackground, defaults.Background);

        var indexed = styler.Style(Cell(TerminalColor.Indexed(1), TerminalColor.Indexed(1)));
        Assert.Equal(0xFF510001u, indexed.Foreground);
        Assert.Equal(0xFF510001u, indexed.Background);

        var bright = styler.Style(Cell(TerminalColor.Indexed(15), TerminalColor.Indexed(15)));
        Assert.Equal(0xFF51000Fu, bright.Foreground);
        Assert.Equal(0xFF51000Fu, bright.Background);
    }

    [Fact]
    public void DimOverATranslucentThemeStaysOpaque()
    {
        var style = Styler(TranslucentStyles)
            .Style(Cell(LiteralFore, TerminalColor.Default, CellAttributes.Dim));

        Assert.Equal(LiteralForeDimmed, style.Foreground);
    }

    [Fact]
    public void DimMixesTowardTheBackgroundTheCellIsDrawnOn()
    {
        var style = Styler().Style(Cell(LiteralFore, LiteralBack, CellAttributes.Dim));

        Assert.Equal(LiteralPairMidpoint, style.Foreground);
        Assert.Equal(LiteralBackArgb, style.Background);
    }

    // The property the attribute means, rather than one blend target: whatever the cell asked for,
    // a dimmed foreground lands between where it was and what it sits on, so it can only ever end
    // up harder to read. A mix toward anything else breaks this on some cell.
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(200, 100, 50)]
    [InlineData(20, 220, 120)]
    public void DimNeverMovesTheForegroundAwayFromItsBackground(byte red, byte green, byte blue)
    {
        var styler = Styler();
        var background = TerminalColor.Rgb(20, 220, 120);
        var plain = styler.Style(Cell(TerminalColor.Rgb(red, green, blue), background));
        var dimmed = styler.Style(Cell(TerminalColor.Rgb(red, green, blue), background, CellAttributes.Dim));

        for (var shift = 0; shift <= 16; shift += 8)
        {
            var from = (plain.Foreground >> shift) & 0xFFu;
            var onto = (plain.Background >> shift) & 0xFFu;
            var got = (dimmed.Foreground >> shift) & 0xFFu;

            Assert.InRange(got, Math.Min(from, onto), Math.Max(from, onto));
            Assert.True(Distance(got, onto) <= Distance(from, onto),
                $"channel {shift} moved from {from} to {got}, away from {onto}");
        }

        static uint Distance(uint a, uint b) => a > b ? a - b : b - a;
    }

    [Fact]
    public void DimOverADefaultBackgroundMixesTowardTheTheme()
    {
        // The combination a real session hits constantly. Slot 4 is (81, 0, 4) against the theme's
        // (16, 32, 48): the red channel's 48.5 rounds up, the other two land whole.
        var style = Styler().Style(Cell(TerminalColor.Indexed(4), TerminalColor.Default, CellAttributes.Dim));

        Assert.Equal(0xFF31101Au, style.Foreground);
        Assert.Equal(ThemeBackground, style.Background);
    }

    [Fact]
    public void DimRoundsEachChannelUpAtTheHalfway()
    {
        // Every channel of (1,3,5) mixed with the theme's (16,32,48) lands on a .5.
        var style = Styler().Style(Cell(TerminalColor.Rgb(1, 3, 5), TerminalColor.Default, CellAttributes.Dim));

        Assert.Equal(0xFF09121Bu, style.Foreground);
    }

    [Fact]
    public void InverseSwapsTheResolvedPair()
    {
        var style = Styler().Style(Cell(LiteralFore, LiteralBack, CellAttributes.Inverse));

        Assert.Equal(LiteralBackArgb, style.Foreground);
        Assert.Equal(LiteralForeArgb, style.Background);
    }

    [Fact]
    public void HiddenPaintsTheForegroundInTheBackground()
    {
        var style = Styler().Style(Cell(LiteralFore, LiteralBack, CellAttributes.Hidden));

        Assert.Equal(LiteralBackArgb, style.Foreground);
        Assert.Equal(LiteralBackArgb, style.Background);
    }

    [Fact]
    public void InverseHappensBeforeDim()
    {
        // Dimming first would leave the dimmed colour in the background instead.
        var style = Styler().Style(
            Cell(LiteralFore, LiteralBack, CellAttributes.Inverse | CellAttributes.Dim));

        Assert.Equal(LiteralPairMidpoint, style.Foreground);
        Assert.Equal(LiteralForeArgb, style.Background);
    }

    // Not an ordering test, and cannot be one: dimming toward the background and then hiding into
    // it lands in the same place as hiding first. Pinned because hidden has to win outright — a
    // midpoint left in the foreground would make concealed text legible.
    [Fact]
    public void HiddenSubsumesDim()
    {
        var style = Styler().Style(
            Cell(LiteralFore, LiteralBack, CellAttributes.Dim | CellAttributes.Hidden));

        Assert.Equal(LiteralBackArgb, style.Foreground);
        Assert.Equal(LiteralBackArgb, style.Background);
    }

    [Fact]
    public void InverseHappensBeforeHidden()
    {
        // Hiding first would yield the old background twice instead of the old foreground twice.
        var style = Styler().Style(
            Cell(LiteralFore, LiteralBack, CellAttributes.Inverse | CellAttributes.Hidden));

        Assert.Equal(LiteralForeArgb, style.Foreground);
        Assert.Equal(LiteralForeArgb, style.Background);
    }

    [Theory]
    [InlineData(0, 0xFF510000u)]
    [InlineData(1, 0xFF510001u)]
    [InlineData(2, 0xFF510002u)]
    [InlineData(3, 0xFF510003u)]
    [InlineData(4, 0xFF510004u)]
    [InlineData(5, 0xFF510005u)]
    [InlineData(6, 0xFF510006u)]
    [InlineData(7, 0xFF510007u)]
    public void BoldDoesNotBrightenAnIndexedColour(int index, uint expected)
    {
        var styler = Styler();
        var color = TerminalColor.Indexed((byte)index);

        var plain = styler.Style(Cell(color, TerminalColor.Default));
        var bold = styler.Style(Cell(color, TerminalColor.Default, CellAttributes.Bold));

        Assert.Equal(expected, plain.Foreground);
        Assert.Equal(expected, bold.Foreground);
    }

    [Theory]
    [InlineData(CellAttributes.Bold, true, false, false, false)]
    [InlineData(CellAttributes.Italic, false, true, false, false)]
    [InlineData(CellAttributes.Underline, false, false, true, false)]
    [InlineData(CellAttributes.CrossedOut, false, false, false, true)]
    public void EachFormAttributeSetsOnlyItsOwnFlag(
        CellAttributes attribute,
        bool bold,
        bool italic,
        bool underline,
        bool strikeThrough)
    {
        var style = Styler().Style(Cell(TerminalColor.Default, TerminalColor.Default, attribute));

        Assert.Equal(bold, style.Bold);
        Assert.Equal(italic, style.Italic);
        Assert.Equal(underline, style.Underline);
        Assert.Equal(strikeThrough, style.StrikeThrough);
    }

    [Fact]
    public void AllFourFormAttributesCompose()
    {
        var style = Styler().Style(Cell(
            TerminalColor.Default,
            TerminalColor.Default,
            CellAttributes.Bold | CellAttributes.Italic | CellAttributes.Underline | CellAttributes.CrossedOut));

        Assert.True(style.Bold);
        Assert.True(style.Italic);
        Assert.True(style.Underline);
        Assert.True(style.StrikeThrough);
    }

    [Theory]
    [InlineData(CellAttributes.None)]
    [InlineData(CellAttributes.Dim)]
    [InlineData(CellAttributes.Blink)]
    [InlineData(CellAttributes.Inverse)]
    [InlineData(CellAttributes.Hidden)]
    public void ColourAttributesSetNoFormFlag(CellAttributes attribute)
    {
        var style = Styler().Style(Cell(LiteralFore, LiteralBack, attribute));

        Assert.False(style.Bold);
        Assert.False(style.Italic);
        Assert.False(style.Underline);
        Assert.False(style.StrikeThrough);
    }

    [Fact]
    public void BlinkChangesNothing()
    {
        var styler = Styler();

        Assert.Equal(
            styler.Style(Cell(LiteralFore, LiteralBack)),
            styler.Style(Cell(LiteralFore, LiteralBack, CellAttributes.Blink)));

        Assert.Equal(
            styler.Style(Cell(TerminalColor.Indexed(5), TerminalColor.Default, CellAttributes.Bold)),
            styler.Style(Cell(TerminalColor.Indexed(5), TerminalColor.Default, CellAttributes.Bold | CellAttributes.Blink)));
    }

    [Fact]
    public void DarkAndLightDisagreeOnEveryAnsiSlot()
    {
        var dark = AnsiSlots("dark", ThemeStyles.Dark.Terminal.Ansi);
        var light = AnsiSlots("light", ThemeStyles.Light.Terminal.Ansi);

        Assert.All(
            dark.Zip(light),
            pair => Assert.True(
                pair.First.Color != pair.Second.Color,
                $"{pair.First.Name} and {pair.Second.Name} are both 0x{pair.First.Color:X8}"));
    }

    [Fact]
    public void EveryThemeTerminalColourIsOpaque()
    {
        Assert.All(
            NamedColors("dark", ThemeStyles.Dark.Terminal).Concat(NamedColors("light", ThemeStyles.Light.Terminal)),
            entry => Assert.True(
                (entry.Color >> 24) == 0xFF,
                $"{entry.Name} is not opaque: 0x{entry.Color:X8}"));
    }

    [Fact]
    public void EachThemeSeparatesItsDefaultForegroundFromItsBackground()
    {
        Assert.NotEqual(ThemeStyles.Dark.Terminal.DefaultForeground, ThemeStyles.Dark.Terminal.DefaultBackground);
        Assert.NotEqual(ThemeStyles.Light.Terminal.DefaultForeground, ThemeStyles.Light.Terminal.DefaultBackground);
    }

    private static (string Name, uint Color)[] AnsiSlots(string theme, AnsiColors ansi) =>
    [
        ($"{theme}.Ansi.Black", ansi.Black),
        ($"{theme}.Ansi.Red", ansi.Red),
        ($"{theme}.Ansi.Green", ansi.Green),
        ($"{theme}.Ansi.Yellow", ansi.Yellow),
        ($"{theme}.Ansi.Blue", ansi.Blue),
        ($"{theme}.Ansi.Magenta", ansi.Magenta),
        ($"{theme}.Ansi.Cyan", ansi.Cyan),
        ($"{theme}.Ansi.White", ansi.White),
        ($"{theme}.Ansi.BrightBlack", ansi.BrightBlack),
        ($"{theme}.Ansi.BrightRed", ansi.BrightRed),
        ($"{theme}.Ansi.BrightGreen", ansi.BrightGreen),
        ($"{theme}.Ansi.BrightYellow", ansi.BrightYellow),
        ($"{theme}.Ansi.BrightBlue", ansi.BrightBlue),
        ($"{theme}.Ansi.BrightMagenta", ansi.BrightMagenta),
        ($"{theme}.Ansi.BrightCyan", ansi.BrightCyan),
        ($"{theme}.Ansi.BrightWhite", ansi.BrightWhite),
    ];

    private static IEnumerable<(string Name, uint Color)> NamedColors(string theme, TerminalStyles styles) =>
        AnsiSlots(theme, styles.Ansi).Concat(
        [
            ($"{theme}.DefaultForeground", styles.DefaultForeground),
            ($"{theme}.DefaultBackground", styles.DefaultBackground),
            ($"{theme}.Cursor", styles.Cursor),
        ]);
}
