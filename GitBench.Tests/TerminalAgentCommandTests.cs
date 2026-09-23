using GitBench.Features.Pairing;
using GitBench.Features.Terminal;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using Xunit;

namespace GitBench.Tests;

/// <summary>A terminal preset's command is filled in quoted for the shell that runs it, so a goal
/// never reaches the shell as syntax.</summary>
public sealed class TerminalAgentCommandTests
{
    private static readonly TerminalAgentValues Values = new(
        "Add it; rm -rf / it's $HOME",
        "/tmp/p/prompt.md",
        new Uri("http://127.0.0.1:5577/tok/mcp"),
        "/tmp/p/mcp.json",
        "/repo");

    [Fact]
    public void Posix_SingleQuotesEveryValue()
    {
        var command = TerminalAgentCommand.Build("agent --mcp {mcpUrl} {prompt}", ShellFamily.Posix, Values);

        Assert.Equal(@"agent --mcp 'http://127.0.0.1:5577/tok/mcp' 'Add it; rm -rf / it'\''s $HOME'", command);
    }

    [Fact]
    public void PowerShell_DoublesSingleQuotes()
    {
        var command = TerminalAgentCommand.Build("agent {prompt}", ShellFamily.PowerShell, Values);

        Assert.Equal("agent 'Add it; rm -rf / it''s $HOME'", command);
    }

    [Fact]
    public void CommandProcessor_RefusesWhatItCannotQuote()
    {
        var values = Values with { Prompt = "100% done" };

        Assert.Null(TerminalAgentCommand.Build("agent {prompt}", ShellFamily.CommandProcessor, values));
        Assert.Equal("agent \"/tmp/p/prompt.md\"", TerminalAgentCommand.Build("agent {promptFile}", ShellFamily.CommandProcessor, values));
    }

    [Fact]
    public void TheClaudePreset_UsesTheConfigFileAndDisallowsWrites()
    {
        var command = TerminalAgentCommand.Build(TerminalAgentCommand.ClaudeCode, ShellFamily.Posix, Values)!;

        Assert.Contains("--mcp-config '/tmp/p/mcp.json'", command);
        Assert.Contains("--disallowedTools Edit,Write", command);
    }

    [Fact]
    public void ForCommand_RunsTheLineInTheUsersShell()
    {
        var options = ShellCommand.ForCommand("/repo", new PtySize(80, 24), new TerminalRgb(0, 0, 0), "agent go");

        Assert.Equal("agent go", options.Arguments[^1]);
        Assert.Equal("/repo", options.WorkingDirectory);
    }
}
