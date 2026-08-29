using GitBench.Features.Terminal;
using GitBench.Pty;
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

    [Fact]
    public void OnUnix_TheShellIsReachedThroughAProgramThatTakesTheTerminalFirst()
    {
        if (OperatingSystem.IsWindows()) return;

        using var shell = new ShellVariable("/bin/zsh");

        var command = ShellCommand.For("/tmp", new PtySize(80, 24));

        Assert.Equal(Acquirer, command.Executable);
        Assert.Equal([ "-c", AcquireAndExec, "/bin/zsh", "-l" ], command.Arguments);
    }

    [Fact]
    public void OnUnix_TheShellStillGetsTheArgumentsAndTheDirectoryItAskedFor()
    {
        if (OperatingSystem.IsWindows()) return;

        using var shell = new ShellVariable("/bin/fish");

        var command = ShellCommand.For("/tmp/somewhere", new PtySize(100, 30));

        Assert.Equal(["-c", AcquireAndExec, "/bin/fish", "-l"], command.Arguments);
        Assert.Equal("/tmp/somewhere", command.WorkingDirectory);
        Assert.Equal(new PtySize(100, 30), command.Size);
        Assert.Equal("xterm-256color", command.Environment["TERM"]);
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
