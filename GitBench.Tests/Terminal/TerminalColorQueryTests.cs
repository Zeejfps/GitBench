using System.Text;
using GitBench.Features.Terminal;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using GitBench.Terminal.Vt.Adapters;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// The whole path a colour question takes: a program writes it down the pseudo-terminal, and the
/// answer has to come back up the same one.
/// </summary>
/// <remarks>
/// The engine's own spec proves it composes the reply; what is proved here is that the reply is
/// actually written back to the shell, and that the colour in it is the one this application is
/// painting the pane with rather than a default some layer invented.
/// </remarks>
public class TerminalColorQueryTests
{
    static ITerminalPalette PaletteFor(ThemeMode mode) =>
        new ThemeTerminalPalette(new ThemeService(new State<ThemeMode>(mode)));

    static string Expected(ThemeStyles styles)
    {
        var background = styles.Terminal.DefaultBackground;
        var (r, g, b) = ((byte)(background >> 16), (byte)(background >> 8), (byte)background);
        return $"\u001b]11;rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}\u001b\\";
    }

    /// <summary>Runs a program that asks for the background, and returns everything it was sent.</summary>
    static string AskForTheBackground(ITerminalPalette? palette)
    {
        var pty = new RecordingPty(Encoding.UTF8.GetBytes("\u001b]11;?\u001b\\"));
        var dispatcher = new QueueDispatcher();

        using var session = TerminalSession.Start(
            () => pty,
            new XtermSharpEngineFactory(),
            new TerminalSize(80, 24),
            dispatcher,
            palette: palette);

        Assert.True(session.Exited.Wait(TimeSpan.FromSeconds(5)), "The recorded output never finished.");

        // In this order, and all three: the reader hands the bytes to the UI thread, the pump is
        // what feeds them to the engine, and only then is there a reply for the write loop to drain.
        Assert.True(dispatcher.WaitForPost(TimeSpan.FromSeconds(5)), "The reader posted nothing.");
        dispatcher.Pump();
        Assert.True(session.Flush(TimeSpan.FromSeconds(5)), "The reply never reached the shell.");

        return Encoding.UTF8.GetString(pty.Written);
    }

    [Fact]
    public void InLightMode_TheShellIsToldTheSurfaceIsLight()
    {
        var written = AskForTheBackground(PaletteFor(ThemeMode.Light));

        Assert.Equal(Expected(ThemeStyles.Light), written);
    }

    [Fact]
    public void InDarkMode_TheShellIsToldTheSurfaceIsDark()
    {
        var written = AskForTheBackground(PaletteFor(ThemeMode.Dark));

        Assert.Equal(Expected(ThemeStyles.Dark), written);
    }

    /// <remarks>
    /// The regression this whole path exists for. The two themes must not answer the same, or a
    /// program has learned nothing by asking and goes on assuming the dark terminal it defaults to.
    /// </remarks>
    [Fact]
    public void TheAnswerIsNotTheSameInBothThemes()
    {
        Assert.NotEqual(
            AskForTheBackground(PaletteFor(ThemeMode.Light)),
            AskForTheBackground(PaletteFor(ThemeMode.Dark)));
    }

    [Fact]
    public void ASessionWithNoPalette_SendsTheShellNothingAtAll()
    {
        Assert.Equal(string.Empty, AskForTheBackground(palette: null));
    }
}
