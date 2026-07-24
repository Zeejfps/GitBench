using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using Xunit;

namespace GitBench.Tests;

// `.git/worktrees/<name>/…` carries two unrelated facts — "the set of worktrees changed" and "that
// worktree's refs moved" — and the classifier used to squash both into WorktreesChangedMessage.
// That made every `git status` inside a worktree (which rewrites that worktree's own index) drag a
// full `git worktree list` rediscovery of the primary behind it, while an external checkout inside
// a worktree produced no refs signal for anyone at all.
//
// `modules/` already had the per-file whitelist that keeps those apart. These pin that `worktrees/`
// now has it too, and that the neighbouring branches did not move.
public sealed class RepoWatcherClassifierTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-classify-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly ChannelRecorder _seen;
    private readonly RepoWatcher _watcher;

    public RepoWatcherClassifierTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir.Path, ".git"));
        _seen = new ChannelRecorder(_bus);
        var repo = new Repo(Guid.NewGuid(), _dir.Path, "primary");
        _watcher = new RepoWatcher(repo, _dispatcher, _bus, new GateTracker());
    }

    // ---- worktrees: the set changed ----

    [Fact]
    public void The_worktrees_container_itself_changes_the_set()
    {
        Classify("worktrees");

        Assert.Equal(1, _seen.Worktrees);
        Assert.Equal(0, _seen.Refs);
    }

    [Fact]
    public void Adding_or_removing_a_worktree_directory_changes_the_set()
    {
        Classify("worktrees/feature-a");

        Assert.Equal(1, _seen.Worktrees);
        Assert.Equal(0, _seen.Refs);
    }

    // ---- worktrees: that worktree's refs moved ----

    [Theory]
    [InlineData("worktrees/feature-a/HEAD")]
    [InlineData("worktrees/feature-a/ORIG_HEAD")]
    [InlineData("worktrees/feature-a/MERGE_HEAD")]
    [InlineData("worktrees/feature-a/REBASE_HEAD")]
    [InlineData("worktrees/feature-a/refs/bisect/bad")]
    public void A_worktrees_own_refs_route_to_the_refs_channel(string relativePath)
    {
        Classify(relativePath);

        Assert.Equal(1, _seen.Refs);
        Assert.Equal(0, _seen.Worktrees);
    }

    // ---- worktrees: read-side noise ----

    // This is the spurious cascade: a status read inside a worktree refreshes that worktree's index
    // stat cache, and the write lands in the *primary's* .git tree where the primary's gate is open.
    [Theory]
    [InlineData("worktrees/feature-a/index")]
    [InlineData("worktrees/feature-a/index.lock")]
    [InlineData("worktrees/feature-a/logs/HEAD")]
    [InlineData("worktrees/feature-a/gitdir")]
    [InlineData("worktrees/feature-a/commondir")]
    [InlineData("worktrees/feature-a/locked")]
    public void Per_worktree_bookkeeping_files_are_ignored(string relativePath)
    {
        Classify(relativePath);

        Assert.Equal(0, _seen.Total);
    }

    // ---- neighbouring branches must not have moved ----

    [Theory]
    [InlineData("HEAD")]
    [InlineData("packed-refs")]
    [InlineData("FETCH_HEAD")]
    [InlineData("ORIG_HEAD")]
    [InlineData("MERGE_HEAD")]
    [InlineData("refs/heads/main")]
    [InlineData("refs/remotes/origin/main")]
    public void The_primarys_own_refs_still_route_to_the_refs_channel(string relativePath)
    {
        Classify(relativePath);

        Assert.Equal(1, _seen.Refs);
        Assert.Equal(0, _seen.Worktrees);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("index.lock")]
    [InlineData("objects/ab/cdef")]
    [InlineData("logs/HEAD")]
    [InlineData("hooks/pre-commit")]
    public void Read_side_writes_in_the_gitdir_are_still_ignored(string relativePath)
    {
        Classify(relativePath);

        Assert.Equal(0, _seen.Total);
    }

    [Theory]
    [InlineData("modules")]
    [InlineData("modules/sub")]
    [InlineData("modules/sub/HEAD")]
    [InlineData("modules/sub/packed-refs")]
    [InlineData("modules/sub/refs/heads/main")]
    public void Submodule_ref_changes_still_route_to_the_submodules_channel(string relativePath)
    {
        Classify(relativePath);

        Assert.Equal(1, _seen.Submodules);
        Assert.Equal(0, _seen.Refs);
        Assert.Equal(0, _seen.Worktrees);
    }

    [Theory]
    [InlineData("modules/sub/index")]
    [InlineData("modules/sub/logs/HEAD")]
    public void Submodule_read_side_writes_are_still_ignored(string relativePath)
    {
        Classify(relativePath);

        Assert.Equal(0, _seen.Total);
    }

    [Fact]
    public void A_path_outside_the_gitdir_is_not_classified()
    {
        _watcher.ClassifyGitChange(null);

        Pump.DrainFor(_dispatcher, Pump.Settle);
        Assert.Equal(0, _seen.Total);
    }

    private void Classify(string gitRelativePath)
    {
        _watcher.ClassifyGitChange(gitRelativePath);
        Pump.DrainFor(_dispatcher, Pump.Settle);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _dir.Dispose();
    }
}
