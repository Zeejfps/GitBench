namespace GitBench.Pty.Tests;

/// <summary>
/// What the handle gate promises the platform sessions that share it: a handle is never released
/// while a caller is inside it, a caller arriving after teardown is turned away rather than handed a
/// dead handle, and the release happens exactly once however the two race.
/// </summary>
/// <remarks>
/// The type carried no tests while it was Windows-only, and the Windows session's own tests only run
/// on Windows — so these are the whole of the regression net for a type two platforms now depend on,
/// and they run everywhere because nothing here opens anything. What they deliberately do not claim
/// is that <c>Close</c> wakes a blocked caller: it cannot, and each session has to arrange that for
/// itself.
/// </remarks>
public class GatedHandleTests
{
    [Fact]
    public void Close_WithNobodyInside_ReleasesTheHandleImmediately()
    {
        var handle = new TrackingHandle();
        var gate = new GatedHandle<TrackingHandle>(handle);

        gate.Close();

        Assert.Equal(1, handle.Releases);
    }

    [Fact]
    public void Close_WithACallerInside_LeavesTheHandleOpenUntilThatCallerLeaves()
    {
        var handle = new TrackingHandle();
        var gate = new GatedHandle<TrackingHandle>(handle);

        Assert.True(gate.TryEnter(out _));
        gate.Close();

        Assert.Equal(0, handle.Releases);

        gate.Leave();

        Assert.Equal(1, handle.Releases);
    }

    [Fact]
    public void TryEnter_AfterClose_TurnsTheCallerAwayWithoutReleasingTheHandleASecondTime()
    {
        var handle = new TrackingHandle();
        var gate = new GatedHandle<TrackingHandle>(handle);

        gate.Close();

        Assert.False(gate.TryEnter(out var entered));
        Assert.Null(entered);
        Assert.Equal(1, handle.Releases);
    }

    [Fact]
    public void Close_Twice_ReleasesTheHandleOnce()
    {
        var handle = new TrackingHandle();
        var gate = new GatedHandle<TrackingHandle>(handle);

        gate.Close();
        gate.Close();

        Assert.Equal(1, handle.Releases);
    }

    /// <remarks>
    /// The race the gate exists for: a session's reader spins through the gate while teardown closes
    /// it, and no pass may see a handle that has already been released. A descriptor is worse than a
    /// Windows handle here, because the number is reused and the call would land on somebody else's
    /// file rather than failing.
    /// </remarks>
    [Fact]
    public void TryEnter_WhileAnotherThreadCloses_NeverHandsOutAReleasedHandle()
    {
        var handle = new TrackingHandle();
        var gate = new GatedHandle<TrackingHandle>(handle);
        var seenReleased = 0;
        var stop = false;

        var user = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (!gate.TryEnter(out _))
                    return;

                if (handle.Releases != 0)
                    Interlocked.Increment(ref seenReleased);

                gate.Leave();
            }
        })
        {
            IsBackground = true,
            Name = "gated-handle-user",
        };

        user.Start();
        Thread.Sleep(20);
        gate.Close();
        Volatile.Write(ref stop, true);

        Assert.True(user.Join(PtyChild.TeardownPatience), "The gate never turned a spinning caller away after Close.");
        Assert.Equal(0, Volatile.Read(ref seenReleased));
        Assert.Equal(1, handle.Releases);
    }
}
