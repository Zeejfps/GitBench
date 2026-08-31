using GitBench.Features.Terminal;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// How the pane starts a shell. The interesting part is not which shell it picks but what the shell
/// is started as: the pane's whole terminal contract — a resize the program hears, a Ctrl-C that
/// reaches it — rests on the child owning the terminal, and on macOS a pseudo-terminal's slave
/// opened by a spawn file action does not become one.
/// </summary>
public class ShellCommandTests
{
    const string Acquirer = "/bin/sh";
    const string AcquireAndExec = "exec \"$0\" \"$@\"";

    /// <summary>A dark surface, for the tests whose subject is not what colour the pane is.</summary>
    static readonly TerminalRgb DarkBackground = new(0x1E, 0x1F, 0x22);

    [Fact]
    public void OnUnix_TheShellIsReachedThroughAProgramThatTakesTheTerminalFirst()
    {
        if (OperatingSystem.IsWindows()) return;

        using var shell = new ShellVariable("/bin/zsh");

        var command = ShellCommand.For("/tmp", new PtySize(80, 24), DarkBackground);

        Assert.Equal(Acquirer, command.Executable);
        Assert.Equal([ "-c", AcquireAndExec, "/bin/zsh", "-l" ], command.Arguments);
    }

    [Fact]
    public void OnUnix_TheShellStillGetsTheArgumentsAndTheDirectoryItAskedFor()
    {
        if (OperatingSystem.IsWindows()) return;

        using var shell = new ShellVariable("/bin/fish");

        var command = ShellCommand.For("/tmp/somewhere", new PtySize(100, 30), DarkBackground);

        Assert.Equal(["-c", AcquireAndExec, "/bin/fish", "-l"], command.Arguments);
        Assert.Equal("/tmp/somewhere", command.WorkingDirectory);
        Assert.Equal(new PtySize(100, 30), command.Size);
        Assert.Equal("xterm-256color", command.Environment["TERM"]);
    }

    /// <remarks>
    /// The hint for the programs that never ask. A shell prompt framework, or a <c>vim</c> that has
    /// started before its OSC 11 reply lands, reads this and nothing else, and with no answer at all
    /// it assumes a dark terminal — which is how a light pane ends up with a program painting black
    /// bars into it.
    /// </remarks>
    [Fact]
    public void ALightPane_TellsTheShellItsBackgroundIsLight()
    {
        var command = ShellCommand.For("/tmp", new PtySize(80, 24), new TerminalRgb(0xFF, 0xFF, 0xFF));

        Assert.Equal("0;15", command.Environment["COLORFGBG"]);
    }

    [Fact]
    public void ADarkPane_TellsTheShellItsBackgroundIsDark()
    {
        var command = ShellCommand.For("/tmp", new PtySize(80, 24), DarkBackground);

        Assert.Equal("15;0", command.Environment["COLORFGBG"]);
    }

    /// <remarks>
    /// Read off the colour rather than off which theme is selected, so the hint cannot disagree with
    /// what is on screen. Green is the channel that carries most of the luma, which is what decides
    /// a colour that is not obviously either.
    /// </remarks>
    [Fact]
    public void TheHint_FollowsTheColourRatherThanAnyOneChannel()
    {
        var blue = ShellCommand.For("/tmp", new PtySize(80, 24), new TerminalRgb(0x00, 0x00, 0xFF));
        var green = ShellCommand.For("/tmp", new PtySize(80, 24), new TerminalRgb(0x00, 0xFF, 0x00));

        Assert.Equal("15;0", blue.Environment["COLORFGBG"]);
        Assert.Equal("0;15", green.Environment["COLORFGBG"]);
    }

    [Fact]
    public void AChildStartedThroughTheAcquirer_OwnsTheTerminal()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal(new PtyExit.Completed(0), ReadTheTerminal(throughTheAcquirer: true));
    }

    [Fact]
    public void OnMacOs_AChildStartedWithoutTheAcquirer_DoesNotOwnTheTerminal()
    {
        if (!OperatingSystem.IsMacOS()) return;

        Assert.NotEqual(new PtyExit.Completed(0), ReadTheTerminal(throughTheAcquirer: false));
    }

    static PtyExit ReadTheTerminal(bool throughTheAcquirer)
    {
        string[] program = ["/usr/bin/head", "-1", "/dev/tty"];

        using var session = new PtySessionFactory().Start(new PtySessionOptions
        {
            Executable = throughTheAcquirer ? Acquirer : program[0],
            Arguments = throughTheAcquirer
                ? ["-c", AcquireAndExec, .. program]
                : [.. program[1..]],
            WorkingDirectory = "/tmp",
            Size = new PtySize(80, 24),
        });

        session.WriteInput("a line to read\r"u8);

        Assert.True(session.Exited.Wait(TimeSpan.FromSeconds(20)), "The child never exited.");
        return session.Exited.Result;
    }

    sealed class ShellVariable : IDisposable
    {
        readonly string? _previous;

        public ShellVariable(string shell)
        {
            _previous = Environment.GetEnvironmentVariable("SHELL");
            Environment.SetEnvironmentVariable("SHELL", shell);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("SHELL", _previous);
    }
}
