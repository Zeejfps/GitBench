using System.ComponentModel;

namespace GitBench.Pty.Tests;

/// <summary>
/// What a spawned child must find when it starts: a real terminal at the requested size, the
/// requested directory, and exactly the environment the caller described — no more.
/// </summary>
/// <remarks>
/// Every assertion here is containment on the decoded stream, never equality: the stream carries
/// conhost's startup and teardown frames, cursor addressing, and reflow around whatever the child
/// printed. See <see cref="VtText"/>.
/// </remarks>
[Collection(PtyTestCollection.Name)]
public class PtySessionSpawnTests
{
    [PtyFact]
    public void Start_RunsTheChildOnThePseudoTerminal_SoItSeesATtyAtTheRequestedSize()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.PowerShell(
            work.Path,
            "Write-Host ('[tty=' + (-not [Console]::IsOutputRedirected) + ';cols=' + [Console]::WindowWidth + ';rows=' + [Console]::WindowHeight + ']')",
            new PtySize(100, 30)));

        var output = new PtyOutputReader(session.Output);

        Assert.True(
            output.WaitFor("[tty=True;cols=100;rows=30]", PtyChild.Patience),
            $"The child never reported a terminal at 100x30. A child spawned without null std handles "
            + $"bypasses the pseudoconsole and its output never arrives at all. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_RunsTheChildInTheWorkingDirectory()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path, "/c", "cd"));
        var output = new PtyOutputReader(session.Output);

        Assert.True(
            output.WaitFor(work.Token, PtyChild.Patience),
            $"The child never reported the working directory it was asked for ({work.Path}). Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_AppliesTheEnvironmentOverlay()
    {
        using var work = new TempDirectory();

        var options = PtyChild.Cmd(work.Path, "/c", "echo", "[%GITBENCH_PTY_OVERLAID%]") with
        {
            Environment = new Dictionary<string, string?> { ["GITBENCH_PTY_OVERLAID"] = "overlaid-value" },
        };

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session.Output);

        Assert.True(
            output.WaitFor("[overlaid-value]", PtyChild.Patience),
            $"The child did not see the overlaid variable. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_InheritsTheParentEnvironment_WhereTheOverlayIsSilent()
    {
        using var work = new TempDirectory();
        using var inherited = new EnvironmentVariable("GITBENCH_PTY_INHERITED", "inherited-value");

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path, "/c", "echo", "[%GITBENCH_PTY_INHERITED%]"));
        var output = new PtyOutputReader(session.Output);

        Assert.True(
            output.WaitFor("[inherited-value]", PtyChild.Patience),
            $"The child did not inherit a variable this process had set. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_RemovesAnInheritedVariable_WhenTheOverlayValueIsNull()
    {
        using var work = new TempDirectory();
        using var inherited = new EnvironmentVariable("GITBENCH_PTY_REMOVED", "inherited-value");

        var options = PtyChild.Cmd(work.Path, "/c", "echo", "[%GITBENCH_PTY_REMOVED%]") with
        {
            Environment = new Dictionary<string, string?> { ["GITBENCH_PTY_REMOVED"] = null },
        };

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session.Output);

        Assert.True(
            output.WaitFor("[%GITBENCH_PTY_REMOVED%]", PtyChild.Patience),
            $"The variable was still set in the child, so a null overlay value did not remove it. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_MatchesOverlayKeysToInheritedVariables_CaseInsensitivelyOnWindows()
    {
        using var work = new TempDirectory();
        using var inherited = new EnvironmentVariable("GITBENCH_PTY_CASE", "inherited-value");

        var options = PtyChild.Cmd(work.Path, "/c", "echo", "[%GITBENCH_PTY_CASE%]") with
        {
            Environment = new Dictionary<string, string?> { ["gitbench_pty_case"] = "overlaid-value" },
        };

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session.Output);

        Assert.True(
            output.WaitFor("[overlaid-value]", PtyChild.Patience),
            $"An overlay key that differs only in case did not replace the inherited variable. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_PassesTerminalIdentityThroughUntouched()
    {
        using var work = new TempDirectory();

        var options = PtyChild.Cmd(work.Path, "/c", "echo", "[%TERM%;%COLORTERM%]") with
        {
            Environment = new Dictionary<string, string?>
            {
                ["TERM"] = "xterm-256color",
                ["COLORTERM"] = "truecolor",
            },
        };

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session.Output);

        Assert.True(
            output.WaitFor("[xterm-256color;truecolor]", PtyChild.Patience),
            $"The child did not see the terminal identity the caller supplied. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_SetsNoTerminalIdentityOfItsOwn()
    {
        using var work = new TempDirectory();
        using var term = new EnvironmentVariable("TERM", null);
        using var colorTerm = new EnvironmentVariable("COLORTERM", null);

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path, "/c", "echo", "[%TERM%;%COLORTERM%]"));
        var output = new PtyOutputReader(session.Output);

        Assert.True(
            output.WaitFor("[%TERM%;%COLORTERM%]", PtyChild.Patience),
            $"The session invented a TERM or COLORTERM the caller never asked for. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_PassesEachArgumentAsOneArgv_IncludingOnesContainingSpaces()
    {
        using var work = new TempDirectory();
        var script = work.File("argv.ps1", "Write-Host ('[argv=' + ($args -join '|') + ']')\n");

        using var session = PtyChild.Start(PtyChild.PowerShellScript(work.Path, script, "plain", "two words"));
        var output = new PtyOutputReader(session.Output);

        Assert.True(
            output.WaitFor("[argv=plain|two words]", PtyChild.Patience),
            $"Arguments did not arrive as the argv entries they were given as. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_FailsWhenTheExecutableCannotBeFound()
    {
        using var work = new TempDirectory();

        var options = PtyChild.Cmd(work.Path) with { Executable = "gitbench-no-such-program.exe" };

        Assert.Throws<Win32Exception>(() => PtyChild.Start(options));
    }
}
