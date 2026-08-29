using System.Text;

namespace GitBench.Pty.Tests;

/// <summary>
/// How a session ends: whether the child finished or the session tore it down, end of stream on
/// <see cref="IPtySession.ReadOutput"/>, and what the calls that arrive afterwards are owed.
/// </summary>
[Collection(PtyTestCollection.Name)]
public class PtySessionLifetimeTests
{
    [PtyTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(255)]
    public void Exited_CompletesWithTheChildExitCode(int code)
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.ExitsWith(work, code));
        var output = new PtyOutputReader(session);

        Assert.True(
            session.Exited.Wait(PtyChild.Patience),
            $"The child never exited. Terminal showed:\n{output.Describe()}");
        Assert.Equal(new PtyExit.Completed(code), session.Exited.Result);
    }

    /// <remarks>
    /// A POSIX wait status carries eight bits of exit code, so a child that asks to exit with 256 exits
    /// with 0 and one that asks for -1 exits with 255. The truncation belongs to the platform, not to
    /// this session, and the session's job is to report what the platform actually recorded rather than
    /// the number the child had in mind. Windows keeps the full thirty-two bits, which is why this is
    /// not asserted there.
    /// </remarks>
    [UnixPtyTheory]
    [InlineData(256, 0)]
    [InlineData(300, 44)]
    [InlineData(-1, 255)]
    public void Exited_ReportsTheExitCodeTheKernelRecorded_NotTheOneTheChildAskedForOnUnix(int asked, int recorded)
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.ExitsWith(work, asked));
        var output = new PtyOutputReader(session);

        Assert.True(
            session.Exited.Wait(PtyChild.Patience),
            $"The child never exited. Terminal showed:\n{output.Describe()}");
        Assert.Equal(new PtyExit.Completed(recorded), session.Exited.Result);
    }

    /// <remarks>
    /// <para>
    /// A child ended by a signal has no exit code at all: the status word says which signal it was
    /// instead. It is still a child that ended on its own — nothing this session did to it — so it is a
    /// <see cref="PtyExit.Completed"/> rather than a <see cref="PtyExit.TornDown"/>, and the number it
    /// carries is the one every shell reports for the same event, 128 plus the signal.
    /// </para>
    /// <para>
    /// This is the reading that keeps both cases meaning what their documentation says: TornDown is
    /// specifically "the session was disposed while the child was still running", which is not what
    /// happened here. An implementation that decodes only WEXITSTATUS reports 0, which is the same
    /// thing it reports for a clean success.
    /// </para>
    /// </remarks>
    [UnixPtyFact]
    public void Exited_ReportsAChildThatEndedItselfWithASignal_AsCompletedWithTheShellsNumberForItOnUnix()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.KillsItself(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            session.Exited.Wait(PtyChild.Patience),
            $"The child never exited. Terminal showed:\n{output.Describe()}");
        Assert.Equal(new PtyExit.Completed(137), session.Exited.Result);
    }

    [PtyFact]
    public void Output_ReachesEndOfStream_OnceTheChildHasExitedAndTheBufferIsDrained()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Prints(work, "GITBENCH-PTY-LAST-WORD"));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("GITBENCH-PTY-LAST-WORD", PtyChild.Patience),
            $"The child's output never arrived, so there is nothing to drain. Terminal showed:\n{output.Describe()}");
        Assert.True(
            output.WaitForEndOfStream(PtyChild.Patience),
            $"ReadOutput never returned 0 after the child exited, so a reader would block forever — on "
            + $"Linux the read that ends the stream fails with EIO rather than returning 0, and that is "
            + $"end of stream, not a fault to rethrow. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// What the child printed is sitting in the terminal buffer whether or not anyone was there for it,
    /// so a consumer that starts reading late still gets everything.
    /// </remarks>
    [PtyFact]
    public void ReadOutput_StartedOnlyAfterTheChildHasExited_StillCarriesWhatItPrinted()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Prints(work, "GITBENCH-PTY-LAST-WORD"));

        Assert.True(session.Exited.Wait(PtyChild.Patience), "The child never exited.");

        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("GITBENCH-PTY-LAST-WORD", PtyChild.Patience),
            $"Output the child had already written was thrown away when it exited, so a reader that "
            + $"arrives a moment late sees an empty session. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// End of stream is a state, not an event: a finished terminal is a normal outcome and reading one
    /// is not misuse, so the answer stays 0 however many times it is asked for.
    /// </remarks>
    [PtyFact]
    public void ReadOutput_AfterEndOfStream_KeepsReturningZeroInsteadOfThrowing()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Prints(work, "GITBENCH-PTY-DONE"));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitForEndOfStream(PtyChild.Patience),
            $"Output never reached end of stream. Terminal showed:\n{output.Describe()}");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var read = -1;
            var thrown = Bounded.Catch(
                $"Read {attempt} past end of stream",
                PtyChild.Patience,
                () => read = session.ReadOutput(new byte[64]));

            Assert.Null(thrown);
            Assert.Equal(0, read);
        }
    }

    /// <remarks>
    /// The seam doc says end of stream arrives "at or after" <see cref="IPtySession.Exited"/>. On Unix
    /// the reader can see the stream end before the watcher's wait has returned, so what a caller can
    /// actually rely on is the weaker half: whichever of the two arrives first, the other follows.
    /// <c>TerminalViewModel</c> stops claiming keystrokes on the strength of it.
    /// </remarks>
    [PtyFact]
    public void Exited_Completes_EvenWhenTheReaderSawEndOfStreamFirst()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Prints(work, "GITBENCH-PTY-LAST-WORD"));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitForEndOfStream(PtyChild.Patience),
            $"The stream never ended. Terminal showed:\n{output.Describe()}");
        Assert.True(
            session.Exited.Wait(PtyChild.Patience),
            $"The stream ended but Exited never completed, so a caller waiting on it to stop taking "
            + $"input would wait forever. Terminal showed:\n{output.Describe()}");
        Assert.Equal(new PtyExit.Completed(0), session.Exited.Result);
    }

    /// <remarks>
    /// Writing to a terminal whose child is gone answers EIO on both BSD and Linux — measured here. It
    /// reaches <see cref="IPtySession.WriteInput"/> as a keystroke typed a moment too late, which is a
    /// normal thing for a person to do and not something to throw over. The contract names only
    /// <see cref="ObjectDisposedException"/>, and the app's terminal pane catches only that, so a
    /// session that surfaced EIO would throw on the UI thread.
    /// </remarks>
    [PtyFact]
    public void WriteInput_AfterTheChildHasExited_DoesNotThrow()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.ExitsWith(work, 0));
        var output = new PtyOutputReader(session);

        Assert.True(
            session.Exited.Wait(PtyChild.Patience),
            $"The child never exited. Terminal showed:\n{output.Describe()}");
        Assert.True(
            output.WaitForEndOfStream(PtyChild.Patience),
            $"Output never ended. Terminal showed:\n{output.Describe()}");

        var thrown = Bounded.Catch(
            "Write after the child exited",
            PtyChild.Patience,
            () => session.WriteInput(Encoding.UTF8.GetBytes("too-late" + PtyChild.Enter)));

        Assert.Null(thrown);
    }

    [PtyFact]
    public void Resize_AfterTheChildHasExited_DoesNotThrow()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.ExitsWith(work, 0));
        var output = new PtyOutputReader(session);

        Assert.True(
            session.Exited.Wait(PtyChild.Patience),
            $"The child never exited. Terminal showed:\n{output.Describe()}");

        var thrown = Bounded.Catch(
            "Resize after the child exited", PtyChild.Patience, () => session.Resize(new PtySize(120, 40)));

        Assert.Null(thrown);
    }

    /// <remarks>
    /// Disposing must not relabel a child that finished on its own. The two cases of
    /// <see cref="PtyExit"/> exist so that a shell which exited can be offered a restart and one the
    /// session ended cannot, so overwriting Completed with TornDown here breaks the single decision
    /// the type exists to carry.
    /// </remarks>
    [PtyFact]
    public void Dispose_AfterTheChildHasAlreadyExited_DoesNotHangOrRelabelTheExit()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.ExitsWith(work, 0));
        var output = new PtyOutputReader(session);

        Assert.True(
            session.Exited.Wait(PtyChild.Patience),
            $"The child never exited. Terminal showed:\n{output.Describe()}");

        Bounded.Run("Dispose after the child exited", PtyChild.TeardownPatience, session.Dispose);

        Assert.Equal(new PtyExit.Completed(0), session.Exited.Result);
    }

    [PtyFact]
    public void Dispose_Twice_IsSafe()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.SitsSilently(work));

        Bounded.Run("First dispose", PtyChild.TeardownPatience, session.Dispose);
        Bounded.Run("Second dispose", PtyChild.TeardownPatience, session.Dispose);
    }

    [PtyFact]
    public void Write_AfterDispose_ThrowsObjectDisposed()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.SitsSilently(work));
        Bounded.Run("Dispose", PtyChild.TeardownPatience, session.Dispose);

        var thrown = Bounded.Catch(
            "Write after dispose", PtyChild.Patience, () => session.WriteInput(new byte[] { (byte)'x' }));

        Assert.IsType<ObjectDisposedException>(thrown);
    }

    /// <remarks>
    /// An empty write is a no-op while the session is alive, but after disposal it is still a call on a
    /// disposed object: reporting that has to come before the emptiness shortcut, or the two calls
    /// disagree about whether the session exists.
    /// </remarks>
    [PtyFact]
    public void Write_OfNoBytesAfterDispose_ThrowsObjectDisposed()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.SitsSilently(work));
        Bounded.Run("Dispose", PtyChild.TeardownPatience, session.Dispose);

        var thrown = Bounded.Catch(
            "Empty write after dispose",
            PtyChild.Patience,
            () => session.WriteInput(ReadOnlySpan<byte>.Empty));

        Assert.IsType<ObjectDisposedException>(thrown);
    }

    [PtyFact]
    public void Resize_AfterDispose_ThrowsObjectDisposed()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.SitsSilently(work));
        Bounded.Run("Dispose", PtyChild.TeardownPatience, session.Dispose);

        var thrown = Bounded.Catch(
            "Resize after dispose", PtyChild.Patience, () => session.Resize(new PtySize(120, 40)));

        Assert.IsType<ObjectDisposedException>(thrown);
    }

    [PtyFact]
    public void ReadOutput_AfterDispose_ReturnsEndOfStream()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.SitsSilently(work));
        Bounded.Run("Dispose", PtyChild.TeardownPatience, session.Dispose);

        var read = -1;
        var thrown = Bounded.Catch(
            "Read after dispose", PtyChild.Patience, () => read = session.ReadOutput(new byte[16]));

        Assert.Null(thrown);
        Assert.Equal(0, read);
    }
}
