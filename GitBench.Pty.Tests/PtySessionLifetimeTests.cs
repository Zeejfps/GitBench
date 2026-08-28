namespace GitBench.Pty.Tests;

/// <summary>
/// How a session ends: whether the child finished or the session tore it down, end of stream on
/// <see cref="IPtySession.ReadOutput"/>, and a <see cref="IDisposable.Dispose"/> that ends the child
/// without hanging and can be called twice.
/// </summary>
[Collection(PtyTestCollection.Name)]
public class PtySessionLifetimeTests
{
    [PtyTheory]
    [InlineData(0)]
    [InlineData(7)]
    public void Exited_CompletesWithTheChildExitCode(int code)
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path, "/c", "exit", "/b", code.ToString()));
        var output = new PtyOutputReader(session);

        Assert.True(session.Exited.Wait(PtyChild.Patience), $"The child never exited. Terminal showed:\n{output.Describe()}");
        Assert.Equal(new PtyExit.Completed(code), session.Exited.Result);
    }

    [PtyFact]
    public void Output_ReachesEndOfStream_OnceTheChildHasExitedAndTheBufferIsDrained()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path, "/c", "echo", "GITBENCH-PTY-LAST-WORD"));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("GITBENCH-PTY-LAST-WORD", PtyChild.Patience),
            $"The child's output never arrived, so there is nothing to drain. Terminal showed:\n{output.Describe()}");
        Assert.True(
            output.WaitForEndOfStream(PtyChild.Patience),
            $"Output never reached end of stream after the child exited — a reader would block forever. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Dispose_WhileTheChildIsRunning_TearsItDown()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path));
        var output = new PtyOutputReader(session);
        var exited = session.Exited;

        Bounded.Run("Dispose of a live session", PtyChild.Patience, session.Dispose);

        Assert.True(exited.Wait(PtyChild.Patience), $"The child outlived the session. Terminal showed:\n{output.Describe()}");
        Assert.Equal(new PtyExit.TornDown(), exited.Result);
    }

    [PtyFact]
    public void Dispose_AfterTheChildHasAlreadyExited_DoesNotHang()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path, "/c", "exit", "/b", "0"));
        var output = new PtyOutputReader(session);

        Assert.True(session.Exited.Wait(PtyChild.Patience), $"The child never exited. Terminal showed:\n{output.Describe()}");

        Bounded.Run("Dispose after the child exited", PtyChild.Patience, session.Dispose);
    }

    [PtyFact]
    public void Dispose_Twice_IsSafe()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path));

        Bounded.Run("First dispose", PtyChild.Patience, session.Dispose);
        Bounded.Run("Second dispose", PtyChild.Patience, session.Dispose);
    }

    [PtyFact]
    public void Write_AfterDispose_ThrowsObjectDisposed()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path));
        Bounded.Run("Dispose", PtyChild.Patience, session.Dispose);

        var thrown = Bounded.Catch("Write after dispose", PtyChild.Patience, () => session.WriteInput(new byte[] { (byte)'x' }));

        Assert.IsType<ObjectDisposedException>(thrown);
    }

    [PtyFact]
    public void Resize_AfterDispose_ThrowsObjectDisposed()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path));
        Bounded.Run("Dispose", PtyChild.Patience, session.Dispose);

        var thrown = Bounded.Catch("Resize after dispose", PtyChild.Patience, () => session.Resize(new PtySize(120, 40)));

        Assert.IsType<ObjectDisposedException>(thrown);
    }

    [PtyFact]
    public void ReadOutput_AfterDispose_ReturnsEndOfStream()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Cmd(work.Path));
        Bounded.Run("Dispose", PtyChild.Patience, session.Dispose);

        var read = -1;
        var thrown = Bounded.Catch(
            "Read after dispose", PtyChild.Patience, () => read = session.ReadOutput(new byte[16]));

        Assert.Null(thrown);
        Assert.Equal(0, read);
    }
}
