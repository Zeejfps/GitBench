using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// What a program in the pane is told when it asks what colour the pane is.
/// </summary>
/// <remarks>
/// The answer has to be the one the renderer is actually using, and it has to keep being that after
/// the user switches themes: a terminal is the one surface in this application that outlives the
/// look of it, because the shell in it is still running.
/// </remarks>
public class ThemeTerminalPaletteTests
{
    static (ThemeTerminalPalette Palette, State<ThemeMode> Mode) Palette(ThemeMode mode)
    {
        var state = new State<ThemeMode>(mode);
        return (new ThemeTerminalPalette(new ThemeService(state)), state);
    }

    static TerminalRgb Rgb(uint argb) => new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    [Fact]
    public void TheBackground_IsTheSurfaceTheRendererPaints()
    {
        var (palette, _) = Palette(ThemeMode.Light);

        Assert.Equal(
            Rgb(ThemeStyles.Light.Terminal.DefaultBackground),
            palette.Resolve(TerminalColorSlot.Background));
    }

    [Fact]
    public void TheForegroundAndCursor_ComeFromTheSameStylesTheCellsDo()
    {
        var (palette, _) = Palette(ThemeMode.Light);

        Assert.Equal(
            Rgb(ThemeStyles.Light.Terminal.DefaultForeground),
            palette.Resolve(TerminalColorSlot.Foreground));
        Assert.Equal(
            Rgb(ThemeStyles.Light.Terminal.Cursor),
            palette.Resolve(TerminalColorSlot.Cursor));
    }

    /// <remarks>
    /// The reason this is a live read and not a value captured when the pane opened. A shell started
    /// in the dark theme and still running in the light one must not keep telling programs the
    /// surface is dark.
    /// </remarks>
    [Fact]
    public void SwitchingTheme_ChangesWhatTheNextProgramIsTold()
    {
        var (palette, mode) = Palette(ThemeMode.Dark);

        var beforeSwitch = palette.Resolve(TerminalColorSlot.Background);
        mode.Value = ThemeMode.Light;
        var afterSwitch = palette.Resolve(TerminalColorSlot.Background);

        Assert.Equal(Rgb(ThemeStyles.Dark.Terminal.DefaultBackground), beforeSwitch);
        Assert.Equal(Rgb(ThemeStyles.Light.Terminal.DefaultBackground), afterSwitch);
    }

    [Fact]
    public void TheTwoThemesDoNotReportTheSameSurface()
    {
        var (dark, _) = Palette(ThemeMode.Dark);
        var (light, _) = Palette(ThemeMode.Light);

        Assert.NotEqual(
            dark.Resolve(TerminalColorSlot.Background),
            light.Resolve(TerminalColorSlot.Background));
    }
}
