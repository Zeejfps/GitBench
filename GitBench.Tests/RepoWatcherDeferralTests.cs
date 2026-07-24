using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using Xunit;

namespace GitBench.Tests;

// A filesystem event that arrives while the app happens to be running git on the same repo used to
// be discarded outright, and nothing anywhere re-polled — so the edit stayed invisible until an
// unrelated event happened to fire. On a slow disk a git read takes seconds, so that window covers
// a large fraction of the time and the drop is routine rather than exotic.
//
// These tests pin the inversion: arrival always records the signal, and the activity gate may only
// postpone the broadcast. Every path from "event arrived" to "nothing ever happens" must be gone.
public sealed class RepoWatcherDeferralTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-watcher-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly GateTracker _gate = new();
    private readonly ChannelRecorder _seen;
    private readonly RepoWatcher _watcher;

    public RepoWatcherDeferralTests()
    {
        // Create the tree before the watcher exists so setup churn can't be mistaken for a signal.
        Directory.CreateDirectory(Path.Combine(_dir.Path, ".git"));
        File.WriteAllText(Path.Combine(_dir.Path, "tracked.txt"), "before");

        _seen = new ChannelRecorder(_bus);
        var repo = new Repo(Guid.NewGuid(), _dir.Path, "watched");
        _watcher = new RepoWatcher(repo, _dispatcher, _bus, _gate);
    }

    [Fact]
    public void An_event_with_no_git_read_in_flight_broadcasts_after_the_debounce()
    {
        _watcher.ClassifyGitChange("HEAD");

        Pump.WaitFor(_dispatcher, () => _seen.Refs == 1, "the refs broadcast");
    }

    [Fact]
    public void An_event_arriving_during_a_git_read_is_deferred_not_dropped()
    {
        _gate.Active = true;

        _watcher.ClassifyGitChange("HEAD");
        Pump.DrainFor(_dispatcher, Pump.Settle);
        Assert.Equal(0, _seen.Refs);

        _gate.Active = false;

        Pump.WaitFor(_dispatcher, () => _seen.Refs == 1, "the deferred refs broadcast");
    }

    [Fact]
    public void A_deferred_signal_survives_many_debounce_cycles()
    {
        _gate.Active = true;
        _watcher.ClassifyGitChange("HEAD");

        // Roughly six debounce cycles: a drain that re-arms must keep re-arming, not give up.
        Pump.DrainFor(_dispatcher, TimeSpan.FromMilliseconds(1600));
        Assert.Equal(0, _seen.Refs);

        _gate.Active = false;
        Pump.WaitFor(_dispatcher, () => _seen.Refs == 1, "the long-deferred refs broadcast");

        Pump.DrainFor(_dispatcher, Pump.Settle);
        Assert.Equal(1, _seen.Refs);
    }

    [Fact]
    public void A_burst_during_a_git_read_collapses_into_one_broadcast()
    {
        _gate.Active = true;
        for (var i = 0; i < 8; i++)
        {
            _watcher.ClassifyGitChange("refs/heads/main");
            Thread.Sleep(20);
        }

        _gate.Active = false;
        Pump.WaitFor(_dispatcher, () => _seen.Refs == 1, "the coalesced refs broadcast");

        Pump.DrainFor(_dispatcher, Pump.Settle);
        Assert.Equal(1, _seen.Refs);
    }

    [Fact]
    public void Channels_defer_independently_of_each_other()
    {
        _gate.Active = true;
        _watcher.ClassifyGitChange("HEAD");
        _watcher.ClassifyGitChange("worktrees");
        _watcher.ClassifyGitChange("modules/sub/HEAD");
        Pump.DrainFor(_dispatcher, Pump.Settle);
        Assert.Equal(0, _seen.Total);

        _gate.Active = false;

        Pump.WaitFor(
            _dispatcher,
            () => _seen.Refs == 1 && _seen.Worktrees == 1 && _seen.Submodules == 1,
            "all three deferred channels");
    }

    [Fact]
    public void A_channel_that_broadcast_does_not_broadcast_again_on_its_own()
    {
        _watcher.ClassifyGitChange("HEAD");
        Pump.WaitFor(_dispatcher, () => _seen.Refs == 1, "the refs broadcast");

        // The pending flag must clear on delivery; a re-armed timer with nothing pending is silent.
        Pump.DrainFor(_dispatcher, TimeSpan.FromMilliseconds(1200));
        Assert.Equal(1, _seen.Refs);
    }

    // FSW's internal buffer overflows exactly when thousands of files churn — a checkout or a build
    // — which is precisely when a git process is running. The recovery path used to be dropped by
    // the same gate that caused the loss it exists to repair.
    [Fact]
    public void Buffer_overflow_recovery_survives_the_activity_gate()
    {
        _gate.Active = true;

        _watcher.ScheduleAllChannels();
        Pump.DrainFor(_dispatcher, Pump.Settle);
        Assert.Equal(0, _seen.Total);

        _gate.Active = false;

        Pump.WaitFor(
            _dispatcher,
            () => _seen.WorkingTree == 1 && _seen.Refs == 1 && _seen.Worktrees == 1 && _seen.Submodules == 1,
            "all four channels to reconcile");
    }

    [Fact]
    public void Disposal_cancels_a_deferred_broadcast_without_throwing()
    {
        _gate.Active = true;
        _watcher.ScheduleAllChannels();

        _watcher.Dispose();
        _gate.Active = false;

        Pump.DrainFor(_dispatcher, TimeSpan.FromMilliseconds(1200));
        Assert.Equal(0, _seen.Total);
    }

    // The three tests above drive the classifier directly. This one goes through the real
    // FileSystemWatcher so the arrival path itself is covered: a genuine editor save landing mid-read
    // must still reach the working-tree channel.
    [Fact]
    public void A_real_file_edit_during_a_git_read_still_reaches_the_working_tree_channel()
    {
        _gate.Active = true;

        File.WriteAllText(Path.Combine(_dir.Path, "tracked.txt"), "edited while git was reading");
        Pump.DrainFor(_dispatcher, Pump.Settle);
        Assert.Equal(0, _seen.WorkingTree);

        _gate.Active = false;

        Pump.WaitFor(_dispatcher, () => _seen.WorkingTree >= 1, "the deferred working-tree broadcast");
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _dir.Dispose();
    }
}
