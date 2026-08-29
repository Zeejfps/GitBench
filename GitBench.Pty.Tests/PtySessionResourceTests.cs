namespace GitBench.Pty.Tests;

/// <summary>
/// What a run of sessions leaves behind. Every other test here asks whether one session behaved;
/// these ask whether eight of them did, which is the only way a leaked descriptor or a half-unwound
/// spawn is visible at all.
/// </summary>
/// <remarks>
/// Unix-gated because the measurement is a POSIX one: <c>open</c> returns the lowest unused
/// descriptor, so the number a fresh file comes back with is the high-water mark of everything this
/// process has forgotten to close. Windows handle values do not work that way.
/// </remarks>
[Collection(PtyTestCollection.Name)]
public class PtySessionResourceTests
{
    /// <remarks>
    /// Two descriptors per session are at stake and one is easy to miss: the master, and the parent's
    /// own copy of the slave, which has to stay open across the spawn — the winsize ioctl fails
    /// without it — and be closed immediately after. A parent that keeps its copy leaks a descriptor
    /// and, worse, holds the terminal open so the stream never ends.
    /// </remarks>
    [UnixPtyFact]
    public void Start_AndDispose_ReturnEveryDescriptorTheyTookOnUnix()
    {
        using var work = new TempDirectory();

        RunOneCycle(work);
        var before = OpenDescriptors.Lowest();

        Bounded.Run("Eight start-and-dispose cycles", PtyChild.Patience, () =>
        {
            for (var i = 0; i < 8; i++)
                RunOneCycle(work);
        });

        var after = OpenDescriptors.Lowest();

        Assert.True(
            after <= before + 2,
            $"The lowest free descriptor moved from {before} to {after} across eight sessions, so each "
            + $"one kept something: the master, or the parent's copy of the slave that has to be closed "
            + $"once the child has it.");
    }

    /// <remarks>
    /// The throwing path is the one nobody walks by hand. A spawn that opens the pseudo-terminal, opens
    /// the slave to set the winsize, and only then fails to exec has two descriptors and a half-built
    /// session to unwind, and the caller only ever sees the exception.
    /// </remarks>
    [UnixPtyFact]
    public void Start_ThatFailsToFindTheExecutable_ReturnsEveryDescriptorItTookOnUnix()
    {
        using var work = new TempDirectory();

        Assert.Throws<PtySpawnException>(() => PtyChild.Start(PtyChild.NoSuchProgram(work)));
        var before = OpenDescriptors.Lowest();

        Bounded.Run("Eight failed spawns", PtyChild.Patience, () =>
        {
            for (var i = 0; i < 8; i++)
                Assert.Throws<PtySpawnException>(() => PtyChild.Start(PtyChild.NoSuchProgram(work)));
        });

        var after = OpenDescriptors.Lowest();

        Assert.True(
            after <= before + 2,
            $"The lowest free descriptor moved from {before} to {after} across eight failed spawns, so "
            + $"the pseudo-terminal opened before the missing program was discovered is still open.");
    }

    static void RunOneCycle(TempDirectory work)
    {
        var session = PtyChild.Start(PtyChild.ExitsWith(work, 0));

        try
        {
            session.Exited.Wait(PtyChild.Patience);
        }
        finally
        {
            session.Dispose();
        }
    }
}
