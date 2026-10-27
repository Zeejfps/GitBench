using GitBench.Features.Terminal;
using Zdev.Pty;
using Zdev.Terminal.Vt;
using Xunit;

namespace GitBench.Tests;

/// <summary>A command run in a terminal pane goes to the user's own shell, started in the repository.</summary>
public sealed class ShellCommandTests
{
    [Fact]
    public void ForCommand_RunsTheLineInTheUsersShell()
    {
        var options = ShellCommand.ForCommand("/repo", new PtySize(80, 24), new TerminalRgb(0, 0, 0), "agent go");

        Assert.Equal("agent go", options.Arguments[^1]);
        Assert.Equal("/repo", options.WorkingDirectory);
    }
}
