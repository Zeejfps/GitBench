using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using Xunit;

namespace GitBench.Tests;

// §7's adaptive debounce: the arrival coalescing window scales toward this repo's own status-read
// cost (floor 250ms, ceiling 2000ms, fast-attack/slow-decay EWMA), while the deferral re-arm stays
// pinned at the floor. The first seven cases drive the internal CurrentDebounceMs() directly — it is
// deterministic in isolation, so faking the timing source beats testing the OS clock. The last goes
// through the real Channel timers to pin the split: a deferred drain's re-check latency is the floor,
// not the scaled arrival window.
public sealed class RepoWatcherDebounceTests : IDisposable
{
    private const int Floor = 250;
    private const int Ceiling = 2000;

    private readonly TempDir _dir = new("gitbench-debounce-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly GateTracker _gate = new();
    private readonly FakeReadGate _readGate = new();
    private readonly ChannelRecorder _seen;
    private readonly Guid _repoId = Guid.NewGuid();
    private readonly RepoWatcher _watcher;

    public RepoWatcherDebounceTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir.Path, ".git"));
        File.WriteAllText(Path.Combine(_dir.Path, "tracked.txt"), "before");

        _seen = new ChannelRecorder(_bus);
        var repo = new Repo(_repoId, _dir.Path, "watched");
        _watcher = new RepoWatcher(repo, _dispatcher, _bus, _gate, _readGate);
    }

    private void SetReadMs(double ms) => _readGate.SetStatusDuration(_repoId, TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void Null_timing_falls_back_to_the_floor()
    {
        Assert.Equal(Floor, _watcher.CurrentDebounceMs());
    }

    [Fact]
    public void A_large_reading_scales_the_window_up()
    {
        SetReadMs(1500);

        Assert.Equal(1500, _watcher.CurrentDebounceMs());
        Assert.True(_watcher.CurrentDebounceMs() > Floor);
    }

    [Fact]
    public void The_floor_is_respected()
    {
        SetReadMs(30);

        Assert.Equal(Floor, _watcher.CurrentDebounceMs());
    }

    [Fact]
    public void The_ceiling_caps_a_pathological_reading()
    {
        SetReadMs(30_000);

        Assert.Equal(Ceiling, _watcher.CurrentDebounceMs());
    }

    [Fact]
    public void Slow_decay_does_not_let_one_fast_read_collapse_the_window()
    {
        SetReadMs(4000);
        Assert.Equal(Ceiling, _watcher.CurrentDebounceMs());

        // A single warm-cache read must not drop the window to the floor: 0.25*100 + 0.75*4000 = 3025,
        // still clamped to the ceiling and emphatically not 250.
        SetReadMs(100);
        var afterOneFast = _watcher.CurrentDebounceMs();
        Assert.Equal(Ceiling, afterOneFast);
        Assert.NotEqual(Floor, afterOneFast);

        // Distinct fast readings each fold once and walk the window down gradually, never in one jump.
        SetReadMs(90);
        _watcher.CurrentDebounceMs();
        SetReadMs(80);
        _watcher.CurrentDebounceMs();
        SetReadMs(70);
        var afterMore = _watcher.CurrentDebounceMs();

        Assert.True(afterMore < Ceiling, $"expected the window to relax below the ceiling, got {afterMore}");
        Assert.True(afterMore > Floor, $"expected the window to stay above the floor, got {afterMore}");
    }

    [Fact]
    public void Fast_attack_reacts_immediately()
    {
        SetReadMs(30);
        Assert.Equal(Floor, _watcher.CurrentDebounceMs());

        // One slow read jumps to the ceiling on the very next call, not after several folds.
        SetReadMs(4000);
        Assert.Equal(Ceiling, _watcher.CurrentDebounceMs());
    }

    [Fact]
    public void Each_read_folds_once()
    {
        SetReadMs(4000);

        var first = _watcher.CurrentDebounceMs();
        for (var i = 0; i < 4; i++)
            Assert.Equal(first, _watcher.CurrentDebounceMs());
    }

    // The split: the arrival window scaled up (large fake reading), but a drain deferred behind a
    // closed activity gate re-checks at the fixed floor — so once the gate reopens it broadcasts
    // within ~one 250ms cycle, not within one ceiling-sized cycle. Window kept generous to stay
    // non-flaky, following An_event_arriving_during_a_git_read_is_deferred_not_dropped's pattern.
    [Fact]
    public void The_deferral_re_arm_stays_at_the_floor_regardless_of_read_cost()
    {
        SetReadMs(5000);   // arrival window clamps to the 2000ms ceiling
        _gate.Active = true;

        _watcher.ClassifyGitChange("HEAD");

        // Past the scaled arrival arm and into the fixed-floor re-arm loop, still deferred.
        Pump.DrainFor(_dispatcher, TimeSpan.FromMilliseconds(2700));
        Assert.Equal(0, _seen.Refs);

        _gate.Active = false;
        var sw = Stopwatch.StartNew();
        Pump.WaitFor(_dispatcher, () => _seen.Refs == 1, "the deferred refs broadcast after the gate reopens");
        sw.Stop();

        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(1000),
            $"deferred broadcast took {sw.ElapsedMilliseconds}ms — the re-arm scaled with read cost instead of staying at the floor");
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _dir.Dispose();
    }
}
