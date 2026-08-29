using GitBench.Pty.Platforms.Unix;

namespace GitBench.Pty.Tests;

/// <summary>
/// The errno decisions a Unix session makes, asserted as a table on every host.
/// </summary>
/// <remarks>
/// These are the answers that cannot otherwise be checked here: Linux ends a terminal's output with
/// EIO where macOS returns 0, and an interrupted system call arrives on a schedule no test can force.
/// Pulling the decisions out of the syscall loops is what makes them assertable at all. It tests the
/// choices, not the plumbing — nothing here proves the loops consult this table — but the plumbing is
/// exercised by every spawn test on this machine, and the wrong-constant and copy-paste mistakes all
/// live in the table.
/// </remarks>
public class UnixErrnoTests
{
    /// <remarks>
    /// The Linux end-of-stream path, which macOS never takes: there the read simply returns 0 and this
    /// decision is never consulted, so an implementation that omitted it would look correct here.
    /// </remarks>
    [Fact]
    public void EndsTheOutputStream_ForEio_BecauseThatIsHowLinuxSaysTheTerminalIsFinished() =>
        Assert.True(UnixErrno.EndsTheOutputStream(UnixErrno.EIO));

    [Theory]
    [InlineData(UnixErrno.EINTR)]
    [InlineData(UnixErrno.EACCES)]
    [InlineData(UnixErrno.EPIPE)]
    [InlineData(UnixErrno.ENOTTY)]
    public void EndsTheOutputStream_ForNothingElse(int errno) =>
        Assert.False(UnixErrno.EndsTheOutputStream(errno));

    [Fact]
    public void ShouldRetry_ForEintr() => Assert.True(UnixErrno.ShouldRetry(UnixErrno.EINTR));

    /// <remarks>
    /// A retry table that is too eager is worse than one that is too narrow: reissuing a call that
    /// failed for a real reason spins.
    /// </remarks>
    [Theory]
    [InlineData(UnixErrno.EIO)]
    [InlineData(UnixErrno.EPIPE)]
    [InlineData(UnixErrno.EACCES)]
    [InlineData(0)]
    public void ShouldRetry_ForNothingElse(int errno) => Assert.False(UnixErrno.ShouldRetry(errno));

    [Theory]
    [InlineData(UnixErrno.EIO)]
    [InlineData(UnixErrno.EPIPE)]
    public void ChildIsGone_ForTheTwoWaysATerminalReportsIt(int errno) =>
        Assert.True(UnixErrno.ChildIsGone(errno));

    /// <remarks>
    /// The failure this exists to forbid: a write loop that swallows every errno leaves a terminal
    /// silently dead, and the suite stays green because a live child is the only thing it ever writes to.
    /// </remarks>
    [Theory]
    [InlineData(UnixErrno.EINTR)]
    [InlineData(UnixErrno.EACCES)]
    [InlineData(UnixErrno.ENOTTY)]
    [InlineData(UnixErrno.E2BIG)]
    public void ChildIsGone_ForNothingElse(int errno) => Assert.False(UnixErrno.ChildIsGone(errno));

    [Theory]
    [InlineData(UnixErrno.ENOENT, PtySpawnFailure.ExecutableNotFound)]
    [InlineData(UnixErrno.EACCES, PtySpawnFailure.AccessDenied)]
    [InlineData(UnixErrno.EPERM, PtySpawnFailure.AccessDenied)]
    [InlineData(UnixErrno.E2BIG, PtySpawnFailure.Other)]
    [InlineData(UnixErrno.ENOTTY, PtySpawnFailure.Other)]
    [InlineData(UnixErrno.EIO, PtySpawnFailure.Other)]
    public void ToSpawnFailure_MapsWhatTheCallerCanActOnAndNothingElse(int errno, PtySpawnFailure expected) =>
        Assert.Equal(expected, UnixErrno.ToSpawnFailure(errno));
}
