using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// The read gate is the one throttle every background git read shares (§6): it bounds concurrency so a
// many-repo tree can't seek-thrash one disk, and times each read per (repo, kind) so §7 can read the
// status-read cost specifically. These pin admission, FIFO wake, the per-kind timing seam, and clean
// disposal — all against the gate directly, holding permits in place of a real read.
public sealed class GitReadGateTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Admits_exactly_the_max_and_the_next_blocks_until_a_release()
    {
        var gate = new GitReadGate();
        var held = new List<IGitReadGate.Permit>();
        for (var i = 0; i < GitReadGate.MaxConcurrentReads; i++)
            held.Add(await gate.Acquire(Guid.NewGuid(), GitReadKind.Status));

        var overflow = gate.Acquire(Guid.NewGuid(), GitReadKind.Status);
        await Task.Delay(100);
        Assert.False(overflow.IsCompleted, "the (N+1)th read must block while the gate is full");

        held[0].Dispose();
        var freed = await overflow.WaitAsync(Wait);

        freed.Dispose();
        for (var i = 1; i < held.Count; i++) held[i].Dispose();
    }

    [Fact]
    public async Task A_released_permit_wakes_exactly_one_waiter_in_fifo_order()
    {
        var gate = new GitReadGate();
        var held = new List<IGitReadGate.Permit>();
        for (var i = 0; i < GitReadGate.MaxConcurrentReads; i++)
            held.Add(await gate.Acquire(Guid.NewGuid(), GitReadKind.Status));

        var first = gate.Acquire(Guid.NewGuid(), GitReadKind.Commits);
        await Task.Delay(50);
        var second = gate.Acquire(Guid.NewGuid(), GitReadKind.Commits);
        await Task.Delay(50);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        // One release frees exactly one waiter, and FIFO means it is the one that queued first.
        held[0].Dispose();
        var firstPermit = await first.WaitAsync(Wait);
        await Task.Delay(50);
        Assert.False(second.IsCompleted, "a single release must wake only one waiter");

        firstPermit.Dispose();
        (await second.WaitAsync(Wait)).Dispose();
        for (var i = 1; i < held.Count; i++) held[i].Dispose();
    }

    // ---- outstanding reads: what the RepoBar row spinner is projected from ----

    [Fact]
    public async Task A_read_is_outstanding_from_when_it_is_asked_for_until_its_permit_is_released()
    {
        var gate = new GitReadGate();
        var repo = Guid.NewGuid();
        Assert.False(gate.HasOutstandingReads(repo));

        var permit = await gate.Acquire(repo, GitReadKind.Status);
        Assert.True(gate.HasOutstandingReads(repo));

        permit.Dispose();
        Assert.False(gate.HasOutstandingReads(repo));
    }

    // The queue wait is the part of a read the user actually notices on a slow disk, so a read
    // parked on a full gate has to count — going by admission would blank the spinner for exactly
    // the wait it exists to report.
    [Fact]
    public async Task A_read_still_waiting_for_a_permit_counts_as_outstanding()
    {
        var gate = new GitReadGate();
        var held = new List<IGitReadGate.Permit>();
        for (var i = 0; i < GitReadGate.MaxConcurrentReads; i++)
            held.Add(await gate.Acquire(Guid.NewGuid(), GitReadKind.Status));

        var waiting = Guid.NewGuid();
        var parked = gate.Acquire(waiting, GitReadKind.Status);
        await Task.Delay(50);

        Assert.False(parked.IsCompleted, "the read must still be waiting for this to prove anything");
        Assert.True(gate.HasOutstandingReads(waiting));

        held[0].Dispose();
        (await parked.WaitAsync(Wait)).Dispose();
        Assert.False(gate.HasOutstandingReads(waiting));

        for (var i = 1; i < held.Count; i++) held[i].Dispose();
    }

    [Fact]
    public async Task One_repo_stays_outstanding_until_its_last_read_finishes()
    {
        var gate = new GitReadGate();
        var repo = Guid.NewGuid();

        var first = await gate.Acquire(repo, GitReadKind.Status);
        var second = await gate.Acquire(repo, GitReadKind.Commits);

        first.Dispose();
        Assert.True(gate.HasOutstandingReads(repo));

        second.Dispose();
        Assert.False(gate.HasOutstandingReads(repo));
    }

    // A read is counted before it is admitted, so an Acquire that fails outright has already been
    // counted and must undo it — otherwise a row spins forever for a read that never ran.
    [Fact]
    public async Task A_read_that_never_starts_is_not_left_outstanding()
    {
        var gate = new GitReadGate();
        gate.Dispose();
        var repo = Guid.NewGuid();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => gate.Acquire(repo, GitReadKind.Status));

        Assert.False(gate.HasOutstandingReads(repo));
    }

    [Fact]
    public async Task Last_status_read_duration_is_null_before_a_read_and_reflects_a_timed_one()
    {
        var gate = new GitReadGate();
        var repo = Guid.NewGuid();
        Assert.Null(gate.LastStatusReadDuration(repo));

        using (await gate.Acquire(repo, GitReadKind.Status))
            await Task.Delay(60);

        var duration = gate.LastStatusReadDuration(repo);
        Assert.NotNull(duration);
        Assert.True(duration.Value >= TimeSpan.FromMilliseconds(40), $"expected a timed read, got {duration}");
    }

    [Fact]
    public async Task A_non_status_read_does_not_move_the_status_timing()
    {
        var gate = new GitReadGate();
        var repo = Guid.NewGuid();
        using (await gate.Acquire(repo, GitReadKind.Status))
            await Task.Delay(60);
        var afterStatus = gate.LastStatusReadDuration(repo);

        foreach (var kind in new[] { GitReadKind.Commits, GitReadKind.Branches, GitReadKind.Discovery })
            using (await gate.Acquire(repo, kind))
                await Task.Delay(60);

        Assert.Equal(afterStatus, gate.LastStatusReadDuration(repo));
    }

    [Fact]
    public async Task Disposal_with_a_waiter_pending_does_not_throw_and_leaks_no_permit()
    {
        var gate = new GitReadGate();
        for (var i = 0; i < GitReadGate.MaxConcurrentReads; i++)
            await gate.Acquire(Guid.NewGuid(), GitReadKind.Status);

        // A waiter is parked when the gate is disposed; disposal must not throw, and the parked task
        // is abandoned (never observed) rather than faulting the test.
        var pending = gate.Acquire(Guid.NewGuid(), GitReadKind.Status);
        _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);

        Assert.Null(Record.Exception(gate.Dispose));

        // No global state leaked: a fresh gate still admits exactly the max and blocks the next.
        var fresh = new GitReadGate();
        var held = new List<IGitReadGate.Permit>();
        for (var i = 0; i < GitReadGate.MaxConcurrentReads; i++)
            held.Add(await fresh.Acquire(Guid.NewGuid(), GitReadKind.Status));

        var overflow = fresh.Acquire(Guid.NewGuid(), GitReadKind.Status);
        await Task.Delay(100);
        Assert.False(overflow.IsCompleted);

        held[0].Dispose();
        (await overflow.WaitAsync(Wait)).Dispose();
        for (var i = 1; i < held.Count; i++) held[i].Dispose();
    }
}
