using System.Text;

namespace GitBench.Pty.Tests;

/// <summary>
/// Teardown as the rest of the system meets it: a reader parked in a blocking read, a
/// <see cref="IDisposable.Dispose"/> arriving from whichever thread happens to call it, and the
/// processes a shell leaves behind.
/// </summary>
/// <remarks>
/// Every bound here is <see cref="PtyChild.TeardownPatience"/> rather than
/// <see cref="PtyChild.Patience"/>. The app's terminal pane joins its reader for two seconds and then
/// abandons it, so a session that releases a blocked reader only eventually — by way of some
/// unrelated timeout — leaks that thread for the life of the process while still passing a
/// thirty-second bound. Thread bodies go through <see cref="Record.Exception"/>: an assertion thrown
/// on a raw thread takes the whole test host down and every other result with it.
/// </remarks>
[Collection(PtyTestCollection.Name)]
public class PtySessionTeardownTests
{
    [PtyFact]
    public void Dispose_WhileTheChildIsRunning_TearsItDown()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.SitsSilently(work));
        var output = new PtyOutputReader(session);
        var exited = session.Exited;

        Assert.True(
            output.WaitFor(PtyChild.Ready, PtyChild.Patience),
            $"The child never started. Terminal showed:\n{output.Describe()}");

        Bounded.Run("Dispose of a live session", PtyChild.TeardownPatience, session.Dispose);

        Assert.True(
            exited.Wait(PtyChild.TeardownPatience),
            $"The child outlived the session. Terminal showed:\n{output.Describe()}");
        Assert.Equal(new PtyExit.TornDown(), exited.Result);
    }

    /// <remarks>
    /// This is the one thing Dispose has to get right that nothing else covers: a reader sitting in a
    /// blocking read has no way out of its own, and stranding it leaks the thread for the life of the
    /// process. The child goes quiet after its marker, so by the time Dispose is called the reader is
    /// provably inside a read with nothing to return.
    /// </remarks>
    [PtyFact]
    public void Dispose_WhileAReaderIsBlockedInReadOutput_ReleasesItPromptly()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.SitsSilently(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(PtyChild.Ready, PtyChild.Patience),
            $"The child never started, so no reader ever blocked. Terminal showed:\n{output.Describe()}");

        Bounded.Run("Dispose while a reader is blocked", PtyChild.TeardownPatience, session.Dispose);

        Assert.True(
            output.WaitForEndOfStream(PtyChild.TeardownPatience),
            $"The blocked reader was left in its read, or was released by an exception rather than by "
            + $"end of stream. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// The app disposes its session from the UI thread while its own reader is inside ReadOutput, so a
    /// teardown that joins the reader would deadlock the moment the two are the same thread. Nothing
    /// stops a caller arranging exactly that.
    /// </remarks>
    [PtyFact]
    public void Dispose_CalledFromTheThreadThatIsReading_DoesNotDeadlock()
    {
        using var work = new TempDirectory();

        var session = PtyChild.Start(PtyChild.SitsSilently(work));
        var reachedRead = new ManualResetEventSlim(false);
        Exception? failure = null;

        var reader = new Thread(() => failure = Record.Exception(() =>
        {
            var buffer = new byte[4096];

            while (true)
            {
                var read = session.ReadOutput(buffer);
                if (read <= 0)
                    return;

                if (VtText.Contains(VtText.Decode(buffer.AsSpan(0, read)), PtyChild.Ready))
                {
                    reachedRead.Set();
                    session.Dispose();
                }
            }
        }))
        {
            IsBackground = true,
            Name = "pty-test-self-disposing-reader",
        };

        reader.Start();

        Assert.True(
            reachedRead.Wait(PtyChild.Patience),
            "The reader never saw the child start, so it never reached the disposing read.");
        Assert.True(
            reader.Join(PtyChild.TeardownPatience),
            "Dispose called from inside the reading thread never returned, so teardown waits on the "
            + "reader it is meant to release.");
        Assert.Null(failure);
    }

    /// <remarks>
    /// <c>TerminalViewModel</c> hangs a continuation off <see cref="IPtySession.Exited"/>. A
    /// <see cref="TaskCompletionSource"/> built without <c>RunContinuationsAsynchronously</c> runs
    /// them on whichever thread completed it — the session's own watcher — so a teardown that joins
    /// that watcher is joining itself, and <c>Thread.Join</c> on the current thread neither throws nor
    /// returns early. It burns the whole patience and returns false.
    /// </remarks>
    [PtyFact]
    public void Dispose_FromAContinuationOnExited_DoesNotDeadlock()
    {
        using var work = new TempDirectory();

        var session = PtyChild.Start(PtyChild.ExitsWith(work, 0));
        var output = new PtyOutputReader(session);
        var disposed = new ManualResetEventSlim(false);
        Exception? failure = null;

        session.Exited.ContinueWith(
            _ =>
            {
                failure = Record.Exception(session.Dispose);
                disposed.Set();
            },
            TaskContinuationOptions.ExecuteSynchronously);

        Assert.True(
            disposed.Wait(TimeSpan.FromSeconds(2)),
            $"Dispose called from a continuation on Exited never returned within two seconds. A "
            + $"teardown that joins its own watcher thread blocks for the whole join patience, and "
            + $"this bound is deliberately shorter than that so a self-join is a red test rather than "
            + $"a coin flip between two equal timeouts. Terminal showed:\n{output.Describe()}");
        Assert.Null(failure);
    }

    [PtyFact]
    public void Dispose_FromTwoThreadsAtOnce_TearsDownOnceAndBothCallsReturn()
    {
        using var work = new TempDirectory();

        var session = PtyChild.Start(PtyChild.SitsSilently(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(PtyChild.Ready, PtyChild.Patience),
            $"The child never started. Terminal showed:\n{output.Describe()}");

        var gun = new Barrier(2);
        Exception? other = null;

        var second = new Thread(() => other = Record.Exception(() =>
        {
            gun.SignalAndWait();
            session.Dispose();
        }))
        {
            IsBackground = true,
            Name = "pty-test-second-dispose",
        };

        second.Start();

        Bounded.Run("Two concurrent disposes", PtyChild.TeardownPatience, () =>
        {
            gun.SignalAndWait();
            session.Dispose();
        });

        Assert.True(second.Join(PtyChild.TeardownPatience), "The second concurrent Dispose never returned.");
        Assert.Null(other);
        Assert.True(
            session.Exited.IsCompleted && !session.Exited.IsFaulted,
            $"Exited did not settle cleanly after two concurrent disposes: {session.Exited.Status}.");
    }

    /// <remarks>
    /// A child that finishes while Dispose is on its way in has genuinely completed, and a session that
    /// reported <see cref="PtyExit.TornDown"/> for it would be describing something that did not
    /// happen. Either answer is legal here; what is not legal is hanging, faulting, or never
    /// completing.
    /// </remarks>
    [PtyFact]
    public void Dispose_ArrivingAsTheChildFinishes_CompletesExitedExactlyOnce()
    {
        using var work = new TempDirectory();

        var session = PtyChild.Start(PtyChild.ExitsWith(work, 3));
        var exited = session.Exited;

        Bounded.Run("Dispose racing a finishing child", PtyChild.TeardownPatience, session.Dispose);

        Assert.True(exited.Wait(PtyChild.TeardownPatience), "Exited never completed.");
        Assert.False(exited.IsFaulted, $"Exited faulted instead of completing: {exited.Exception}");
        Assert.True(
            exited.Result is PtyExit.Completed(3) or PtyExit.TornDown,
            $"Exited said {exited.Result}, which is neither the code the child left nor a teardown.");
    }

    /// <remarks>
    /// <para>
    /// A login shell leaves things running behind it, and on Linux anything still holding the slave
    /// keeps the terminal open, so a session that ends only the process it started leaves its reader
    /// blocked forever. Since POSIX_SPAWN_SETSID makes the child a process-group leader, signalling the
    /// group reaches the grandchildren too.
    /// </para>
    /// <para>
    /// The <c>nohup</c> is what makes this discriminating rather than decorative. Measured on this
    /// machine: with it, ending only the direct child leaves the grandchild running; without it, macOS
    /// revokes the terminal when the session leader dies and the grandchild dies whatever the session
    /// does — so the obvious version of this test passes against an implementation that would hang on
    /// Linux.
    /// </para>
    /// </remarks>
    [UnixPtyFact]
    public void Dispose_WithAGrandchildStillHoldingTheTerminal_EndsTheWholeProcessGroupOnUnix()
    {
        using var work = new TempDirectory();
        var pidPath = Path.Combine(work.Path, "grandchild.pid");

        using var session = PtyChild.Start(PtyChild.LeavesADetachedGrandchild(work, pidPath));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[grandchild-ready]", PtyChild.Patience),
            $"The child never reported a grandchild, so there was nothing to orphan. "
            + $"Terminal showed:\n{output.Describe()}");

        var grandchild = int.Parse(File.ReadAllText(pidPath).Trim());
        Assert.True(UnixProcess.IsAlive(grandchild), $"Process {grandchild} was already gone before Dispose.");

        Bounded.Run("Dispose with a detached grandchild", PtyChild.TeardownPatience, session.Dispose);

        Assert.True(
            UnixProcess.WaitForExit(grandchild, PtyChild.TeardownPatience),
            $"Process {grandchild} outlived the session it was started from. Ending only the direct "
            + $"child leaves whatever it started running and, on Linux, holding the terminal open, so "
            + $"the stream never ends. Terminal showed:\n{output.Describe()}");
        Assert.True(
            output.WaitForEndOfStream(PtyChild.TeardownPatience),
            $"The stream never ended even though the process group did. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// Every other teardown test here is satisfied by a session that signals the child and walks away.
    /// A child that was killed and never waited for stays in the process table as a zombie, and
    /// <c>kill(pid, 0)</c> still succeeds for it — which is what makes the difference measurable at
    /// all. In a window where panes open and close all day, the cost is a process slot each.
    /// </remarks>
    [UnixPtyFact]
    public void Dispose_WhileTheChildIsRunning_ReapsItRatherThanLeavingAZombieOnUnix()
    {
        using var work = new TempDirectory();
        var pidPath = Path.Combine(work.Path, "child.pid");

        using var session = PtyChild.Start(PtyChild.RecordsItsOwnPid(work, pidPath));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[recorded]", PtyChild.Patience),
            $"The child never reported its pid. Terminal showed:\n{output.Describe()}");

        var child = int.Parse(File.ReadAllText(pidPath).Trim());

        Bounded.Run("Dispose of a live session", PtyChild.TeardownPatience, session.Dispose);

        Assert.True(
            UnixProcess.WaitForExit(child, PtyChild.TeardownPatience),
            $"Process {child} still exists after teardown, so it was signalled but never waited for. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// A resize is an ioctl on the same descriptor a reader is parked in. A session that serialises the
    /// two behind one lock deadlocks here, and one that closes the descriptor to resize it loses the
    /// reader.
    /// </remarks>
    [PtyFact]
    public void Resize_WhileAReaderIsBlocked_ReachesTheChildAndLeavesTheReaderRunning()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.WatchesForResize(work, new PtySize(80, 24)));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(PtyChild.Ready, PtyChild.Patience),
            $"The child never started. Terminal showed:\n{output.Describe()}");

        Bounded.Run(
            "Resize while a reader is blocked",
            PtyChild.TeardownPatience,
            () => session.Resize(new PtySize(120, 40)));

        Assert.True(
            output.WaitFor("[resized=120x40]", PtyChild.Patience),
            $"The resize never reached the child, or the reader that had to carry its answer was lost. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// <para>
    /// The seam says nothing about concurrent writers and the app only ever writes from one thread, so
    /// this asserts the weakest defensible thing: two at once neither deadlock nor throw. It
    /// deliberately does not assert that the two writes stay unbroken, because nothing promises that
    /// and asserting it would invent a contract.
    /// </para>
    /// <para>
    /// The child has to be one that reads. A terminal's input queue is about a kilobyte, so twenty
    /// kilobytes aimed at a child that never reads its input parks the writer in <c>write(2)</c> until
    /// something drains it — measured with no session involved at all, so it is the line discipline
    /// applying backpressure rather than anything here deadlocking. Two writers blocked on a full
    /// queue would look exactly like the bug this test is for and be neither.
    /// </para>
    /// </remarks>
    [PtyFact]
    public void WriteInput_FromTwoThreadsAtOnce_NeitherDeadlocksNorThrows()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.ReadsContinuously(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(PtyChild.Ready, PtyChild.Patience),
            $"The child never started. Terminal showed:\n{output.Describe()}");

        var gun = new Barrier(2);
        Exception? other = null;

        var second = new Thread(() => other = Record.Exception(() =>
        {
            var payload = Encoding.ASCII.GetBytes(new string('b', 512) + "\n");
            gun.SignalAndWait();

            for (var i = 0; i < 20; i++)
                session.WriteInput(payload);
        }))
        {
            IsBackground = true,
            Name = "pty-test-second-writer",
        };

        second.Start();

        Bounded.Run("Two concurrent writers", PtyChild.Patience, () =>
        {
            var payload = Encoding.ASCII.GetBytes(new string('a', 512) + "\n");
            gun.SignalAndWait();

            for (var i = 0; i < 20; i++)
                session.WriteInput(payload);
        });

        Assert.True(second.Join(PtyChild.Patience), "The second writer never finished.");
        Assert.Null(other);
    }

    /// <remarks>
    /// <c>ptsname</c> returns a pointer into static storage and has no reentrant form on macOS, so two
    /// overlapping spawns need serialising or the second child attaches to the first one's terminal.
    /// A green run is not proof the serialisation is there — the race has to actually lose — but a red
    /// one is proof it is not.
    /// </remarks>
    [UnixPtyFact]
    public void Start_FromTwoThreadsAtOnce_GivesEachChildATerminalOfItsOwnOnUnix()
    {
        using var work = new TempDirectory();

        var firstPath = Path.Combine(work.Path, "first.tty");
        var secondPath = Path.Combine(work.Path, "second.tty");

        IPtySession? first = null;
        IPtySession? second = null;

        try
        {
            var gun = new Barrier(2);
            Exception? secondFailure = null;

            Bounded.Run("Two overlapping spawns", PtyChild.Patience, () =>
            {
                var other = new Thread(() => secondFailure = Record.Exception(() =>
                {
                    gun.SignalAndWait();
                    second = PtyChild.Start(PtyChild.RecordsTerminalName(work, secondPath));
                }))
                {
                    IsBackground = true,
                    Name = "pty-test-second-spawn",
                };

                other.Start();
                gun.SignalAndWait();
                first = PtyChild.Start(PtyChild.RecordsTerminalName(work, firstPath));

                Assert.True(other.Join(PtyChild.Patience), "The second spawn never returned.");
            });

            Assert.Null(secondFailure);

            var firstOutput = new PtyOutputReader(first!);
            var secondOutput = new PtyOutputReader(second!);

            Assert.True(
                firstOutput.WaitFor("[recorded]", PtyChild.Patience),
                $"The first child never reported. Terminal showed:\n{firstOutput.Describe()}");
            Assert.True(
                secondOutput.WaitFor("[recorded]", PtyChild.Patience),
                $"The second child never reported. Terminal showed:\n{secondOutput.Describe()}");

            var firstName = File.ReadAllText(firstPath).Trim();
            var secondName = File.ReadAllText(secondPath).Trim();

            Assert.NotEqual("", firstName);
            Assert.True(
                firstName != secondName,
                $"Both children reported the same terminal, {firstName}, so two overlapping spawns "
                + $"shared one ptsname result and the second child attached to the first one's terminal.");
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
        }
    }
}
