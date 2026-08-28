using System.Text;

namespace GitBench.Pty.Tests;

/// <summary>
/// Bytes in both directions while the child is alive: what it prints reaches <see cref="IPtySession.Output"/>,
/// what we <see cref="IPtySession.Write"/> reaches it as terminal input, and a resize moves the
/// terminal underneath it.
/// </summary>
[Collection(PtyTestCollection.Name)]
public class PtySessionIoTests
{
    [PtyFact]
    public void Output_CarriesWhatTheChildPrints()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path, "/c", "echo", "GITBENCH-PTY-PRINTED"));
        var output = new PtyOutputReader(session.Output);

        Assert.True(
            output.WaitFor("GITBENCH-PTY-PRINTED", PtyChild.Patience),
            $"Nothing the child printed reached Output. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Write_DeliversBytesToTheChildAsTerminalInput()
    {
        using var work = new TempDirectory();

        var options = PtyChild.Cmd(work.Path) with
        {
            Environment = new Dictionary<string, string?> { ["GITBENCH_PTY_TYPED"] = "GITBENCH-PTY-EXPANDED" },
        };

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session.Output);
        WaitForPrompt(output, work);

        session.Write(Encoding.UTF8.GetBytes("echo %GITBENCH_PTY_TYPED%\r\n"));

        Assert.True(
            output.WaitFor("GITBENCH-PTY-EXPANDED", PtyChild.Patience),
            $"The shell never ran the line that was typed at it — the expansion only exists if the child "
            + $"read the input, since the echoed keystrokes contain the variable name, not its value. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Write_ReachesTheChildEvenWhenTheEffectIsNotPrinted()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path));
        var output = new PtyOutputReader(session.Output);
        WaitForPrompt(output, work);

        session.Write(Encoding.UTF8.GetBytes("exit 7\r\n"));

        Assert.True(session.Exited.Wait(PtyChild.Patience), $"The shell did not exit. Terminal showed:\n{output.Describe()}");
        Assert.Equal(7, session.Exited.Result);
    }

    [PtyFact]
    public void Resize_ChangesTheSizeTheChildSees()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.PowerShell(
            work.Path,
            "Write-Host '[ready]'; "
            + "$w = [Console]::WindowWidth; $n = 0; "
            + "while ([Console]::WindowWidth -eq $w -and $n -lt 200) { Start-Sleep -Milliseconds 50; $n = $n + 1 }; "
            + "Write-Host ('[resized=' + [Console]::WindowWidth + 'x' + [Console]::WindowHeight + ']')",
            new PtySize(80, 24)));

        var output = new PtyOutputReader(session.Output);
        Assert.True(output.WaitFor("[ready]", PtyChild.Patience), $"The child never started. Terminal showed:\n{output.Describe()}");

        session.Resize(new PtySize(120, 40));

        Assert.True(
            output.WaitFor("[resized=120x40]", PtyChild.Patience),
            $"The child never saw the terminal change to 120x40. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void ReadingAndWritingConcurrently_LosesNothingAndDoesNotDeadlock()
    {
        using var work = new TempDirectory();

        var options = PtyChild.Cmd(work.Path) with
        {
            Environment = new Dictionary<string, string?> { ["GITBENCH_PTY_TYPED"] = "GITBENCH-PTY-EXPANDED" },
        };

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session.Output);
        WaitForPrompt(output, work);

        Bounded.Run("Fifty writes against a live reader", PtyChild.Patience, () =>
        {
            for (var i = 0; i < 50; i++)
                session.Write(Encoding.UTF8.GetBytes("echo %GITBENCH_PTY_TYPED%\r\n"));

            session.Write(Encoding.UTF8.GetBytes("exit 5\r\n"));
        });

        Assert.True(
            output.WaitFor("GITBENCH-PTY-EXPANDED", PtyChild.Patience),
            $"Nothing written during the burst reached the child. Terminal showed:\n{output.Describe()}");
        Assert.True(session.Exited.Wait(PtyChild.Patience), $"The shell did not exit. Terminal showed:\n{output.Describe()}");
        Assert.Equal(5, session.Exited.Result);
    }

    static void WaitForPrompt(PtyOutputReader output, TempDirectory work) =>
        Assert.True(
            output.WaitFor(work.Token, PtyChild.Patience),
            $"The shell never printed a prompt, so there was nothing to type at. Terminal showed:\n{output.Describe()}");
}
