using System.Text;

namespace GitBench.Pty.Tests;

/// <summary>
/// Bytes in both directions while the child is alive: what it prints reaches
/// <see cref="IPtySession.ReadOutput"/>, what we <see cref="IPtySession.WriteInput"/> reaches it as
/// terminal input, and a resize moves the terminal underneath it. The boundaries of both streams are
/// here too — a read that asks for nothing, a read one byte wide, a write of nothing, and a write far
/// larger than the terminal takes at once.
/// </summary>
[Collection(PtyTestCollection.Name)]
public class PtySessionIoTests
{
    [PtyFact]
    public void Output_CarriesWhatTheChildPrints()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Prints(work, "GITBENCH-PTY-PRINTED"));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("GITBENCH-PTY-PRINTED", PtyChild.Patience),
            $"Nothing the child printed reached ReadOutput. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Output_CarriesWhatTheChildPrintsToStandardError()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.PrintsToStandardError(work, "GITBENCH-PTY-DIAGNOSTIC"));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("GITBENCH-PTY-DIAGNOSTIC", PtyChild.Patience),
            $"Nothing the child wrote to standard error reached ReadOutput, so the second descriptor "
            + $"was never put on the terminal. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Write_DeliversBytesToTheChildAsTerminalInput()
    {
        using var work = new TempDirectory();

        var options = PtyChild.Shell(work).WithVariable("GITBENCH_PTY_TYPED", "GITBENCH-PTY-EXPANDED");

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session);
        PtyChild.WaitForPrompt(output, work);

        PtyChild.Type(session, PtyChild.EchoVariable("GITBENCH_PTY_TYPED"));

        Assert.True(
            output.WaitFor("[GITBENCH_PTY_TYPED=GITBENCH-PTY-EXPANDED]", PtyChild.Patience),
            $"The shell never ran the line that was typed at it — the expansion only exists if the child "
            + $"read the input, since the echoed keystrokes carry the variable's name, not its value. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Write_ReachesTheChildEvenWhenTheEffectIsNotPrinted()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Shell(work));
        var output = new PtyOutputReader(session);
        PtyChild.WaitForPrompt(output, work);

        PtyChild.Type(session, PtyChild.Exit(7));

        Assert.True(
            session.Exited.Wait(PtyChild.Patience),
            $"The shell did not exit. Terminal showed:\n{output.Describe()}");
        Assert.Equal(new PtyExit.Completed(7), session.Exited.Result);
    }

    [PtyFact]
    public void Resize_ChangesTheSizeTheChildSees()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.WatchesForResize(work, new PtySize(80, 24)));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(PtyChild.Ready, PtyChild.Patience),
            $"The child never started. Terminal showed:\n{output.Describe()}");

        session.Resize(new PtySize(120, 40));

        Assert.True(
            output.WaitFor("[resized=120x40]", PtyChild.Patience),
            $"The child never saw the terminal change to 120x40, so the resize did not reach it — on "
            + $"Unix that means the master's winsize was not set or SIGWINCH was never delivered. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// A terminal that is told the size it already has notifies nobody — measured here, the kernel
    /// raises no SIGWINCH for it. The session still has to accept the call and stay usable, because a
    /// window manager sends the same size back constantly during a drag.
    /// </remarks>
    [PtyFact]
    public void Resize_ToTheSizeTheTerminalAlreadyIs_ChangesNothingAndLeavesTheSessionUsable()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.WatchesForResize(work, new PtySize(80, 24)));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(PtyChild.Ready, PtyChild.Patience),
            $"The child never started. Terminal showed:\n{output.Describe()}");

        Bounded.Run("Resize to the current size", PtyChild.Patience, () => session.Resize(new PtySize(80, 24)));

        session.Resize(new PtySize(132, 50));

        Assert.True(
            output.WaitFor("[resized=132x50]", PtyChild.Patience),
            $"A resize to the size the terminal already was left the session unable to resize at all. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void ReadingAndWritingConcurrently_LosesNothingAndDoesNotDeadlock()
    {
        using var work = new TempDirectory();

        var options = PtyChild.Shell(work).WithVariable("GITBENCH_PTY_TYPED", "GITBENCH-PTY-EXPANDED");

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session);
        PtyChild.WaitForPrompt(output, work);

        Bounded.Run("Fifty writes against a live reader", PtyChild.Patience, () =>
        {
            for (var i = 0; i < 50; i++)
                PtyChild.Type(session, PtyChild.EchoVariable("GITBENCH_PTY_TYPED"));

            PtyChild.Type(session, PtyChild.Exit(5));
        });

        Assert.True(
            output.WaitFor("[GITBENCH_PTY_TYPED=GITBENCH-PTY-EXPANDED]", PtyChild.Patience),
            $"Nothing written during the burst reached the child. Terminal showed:\n{output.Describe()}");
        Assert.True(
            session.Exited.Wait(PtyChild.Patience),
            $"The shell did not exit. Terminal showed:\n{output.Describe()}");
        Assert.Equal(new PtyExit.Completed(5), session.Exited.Result);
    }

    /// <remarks>
    /// An empty buffer has no room for a byte, so 0 is the only answer available — which is the same
    /// answer end of stream gives. The one thing it must not do is consume what was waiting, or mark
    /// the stream ended.
    /// </remarks>
    [PtyFact]
    public void ReadOutput_WithAnEmptyBuffer_ReturnsZeroWithoutConsumingAnything()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Prints(work, "GITBENCH-PTY-STILL-THERE"));

        var empty = -1;
        Bounded.Run(
            "Read into an empty buffer", PtyChild.Patience, () => empty = session.ReadOutput(Span<byte>.Empty));

        Assert.Equal(0, empty);

        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("GITBENCH-PTY-STILL-THERE", PtyChild.Patience),
            $"A read into an empty buffer swallowed the output that was waiting, or ended the stream. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// A caller is entitled to a buffer smaller than what the terminal has waiting. An implementation
    /// that reads into a buffer of its own and copies only what fits loses the rest, and the only way
    /// to see it is to make the caller's buffer far too small.
    /// </remarks>
    [PtyFact]
    public void ReadOutput_IntoASingleByteBuffer_LosesNothing()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.Prints(work, "GITBENCH-PTY-BYTE-AT-A-TIME"));

        var received = new MemoryStream();

        Bounded.Run("Drain the session one byte at a time", PtyChild.Patience, () =>
        {
            var one = new byte[1];
            while (session.ReadOutput(one) == 1)
                received.WriteByte(one[0]);
        });

        var decoded = VtText.Decode(received.GetBuffer().AsSpan(0, (int)received.Length));

        Assert.True(
            VtText.Contains(decoded, "GITBENCH-PTY-BYTE-AT-A-TIME"),
            $"Reading one byte at a time did not reassemble what the child printed. Terminal showed:\n{decoded}");
    }

    /// <remarks>
    /// A silent child rather than a talkative one, and the distinction is the whole test: a reader
    /// that waits for readiness before it looks at the buffer answers correctly whenever output
    /// happens to be waiting, and blocks forever when none is coming. The emptiness shortcut has to
    /// come before the wait, not after it — which is exactly the trap a poll-based wakeup design
    /// walks into.
    /// </remarks>
    [PtyFact]
    public void ReadOutput_WithAnEmptyBuffer_ReturnsZeroWithoutWaitingForOutputThatIsNotComing()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.SitsSilently(work));

        var drained = new MemoryStream();

        Bounded.Run("Drain the child's marker", PtyChild.Patience, () =>
        {
            var buffer = new byte[4096];

            while (!VtText.Contains(
                VtText.Decode(drained.GetBuffer().AsSpan(0, (int)drained.Length)), PtyChild.Ready))
            {
                var read = session.ReadOutput(buffer);
                if (read <= 0)
                    break;

                drained.Write(buffer, 0, read);
            }
        });

        var empty = -1;
        Bounded.Run(
            "Read into an empty buffer once the child has gone quiet",
            PtyChild.TeardownPatience,
            () => empty = session.ReadOutput(Span<byte>.Empty));

        Assert.Equal(0, empty);
    }

    [PtyFact]
    public void WriteInput_WithNoBytes_DoesNothingAndLeavesTheInputSideUsable()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.ReadsOneLine(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(PtyChild.Ready, PtyChild.Patience),
            $"The child never started. Terminal showed:\n{output.Describe()}");

        Bounded.Run("Write no bytes", PtyChild.Patience, () => session.WriteInput(ReadOnlySpan<byte>.Empty));

        PtyChild.Type(session, "still-listening");

        Assert.True(
            output.WaitFor("[typed=still-listening]", PtyChild.Patience),
            $"A write of no bytes left the input side unusable. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// A terminal takes only as much input at once as its buffer holds — a few kilobytes — so a larger
    /// write returns short and has to be resumed. The child switches the line discipline off first,
    /// because a canonical-mode terminal discards a line longer than MAX_CANON outright: measured
    /// here, four thousand bytes written as one line arrived as none of them, and a test that skipped
    /// the <c>stty raw</c> would be measuring the kernel's line editor rather than WriteInput.
    /// </remarks>
    [UnixPtyFact]
    public void WriteInput_WithMoreBytesThanTheTerminalTakesAtOnce_DeliversAllOfThemOnUnix()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.EchoesRaw(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(PtyChild.Ready, PtyChild.Patience),
            $"The child never started. Terminal showed:\n{output.Describe()}");

        var bulk = Encoding.ASCII.GetBytes(new string('a', 64 * 1024) + "GITBENCH-PTY-BULK-END\n");

        Bounded.Run(
            "Write sixty-four kilobytes at once", PtyChild.Patience, () => session.WriteInput(bulk));

        Assert.True(
            output.WaitFor("GITBENCH-PTY-BULK-END", PtyChild.Patience),
            $"The tail of a 64KiB write never came back from the child, so WriteInput returned after a "
            + $"short write instead of resuming it. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// A bare newline is what a program pasting into a terminal sends, and the Unix line discipline
    /// accepts it as a line terminator just as it accepts the carriage return a keyboard sends. The
    /// session passes both through untouched; it is not in the business of rewriting line endings.
    /// </remarks>
    [UnixPtyFact]
    public void WriteInput_AcceptsALineEndingWithoutACarriageReturnOnUnix()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.ReadsOneLine(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(PtyChild.Ready, PtyChild.Patience),
            $"The child never started. Terminal showed:\n{output.Describe()}");

        session.WriteInput(Encoding.UTF8.GetBytes("newline-only\n"));

        Assert.True(
            output.WaitFor("[typed=newline-only]", PtyChild.Patience),
            $"A line ended with a bare newline never completed a line for the child. "
            + $"Terminal showed:\n{output.Describe()}");
    }
}
