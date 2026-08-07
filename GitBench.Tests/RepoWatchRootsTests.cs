using GitBench.Features.Repos;
using Xunit;

namespace GitBench.Tests;

// The layout that matters here is the one nobody developing GitBench can run: on Linux a recursive
// watch on a working tree spends one inotify watch per directory out of a per-user pool shared with
// the user's editor, file manager and IDE, and exhausting it is a desktop-session outage rather than
// a GitBench one. RepoWatchRoots.For takes the cost as an argument precisely so both layouts are
// reachable from a test on any OS — these run on Windows and pin the Linux branch all the same.
public sealed class RepoWatchRootsTests
{
    // Nothing here touches the disk — RepoWatchRoots.For is string work — but the paths are rooted
    // and platform-shaped so the assertions read the way a real layout would.
    private static readonly string Src = Path.Combine(Path.GetTempPath(), "gitbench-roots");
    private static readonly string WorkingTree = Path.Combine(Src, "repo");
    private static readonly string GitDir = Path.Combine(WorkingTree, ".git");

    // ---- Windows and macOS: one handle covers the tree, so nothing is given up ----

    [Fact]
    public void One_handle_platforms_watch_the_working_tree_recursively_and_nothing_else()
    {
        var roots = RepoWatchRoots.For(WorkingTree, GitDir, WatchCost.RecursiveIsOneHandle);

        var root = Assert.Single(roots);
        Assert.Equal(WorkingTree, root.Path);
        Assert.True(root.Recursive);
        Assert.Equal(WatchRootKind.WorkingTree, root.Kind);
    }

    // The gitdir is inside the tree for a primary repo and outside it for a worktree or a submodule.
    // A single recursive tree watch is the whole layout either way; the resolved gitdir is what the
    // classifier measures paths against, not a root of its own.
    [Fact]
    public void One_handle_platforms_do_not_add_a_root_for_a_gitdir_outside_the_tree()
    {
        var linked = Path.Combine(Src, "primary", ".git", "worktrees", "wt");

        var roots = RepoWatchRoots.For(WorkingTree, linked, WatchCost.RecursiveIsOneHandle);

        Assert.Equal(WorkingTree, Assert.Single(roots).Path);
    }

    // ---- Linux: cost is proportional to refs, not to the working tree ----

    [Fact]
    public void Per_directory_platforms_never_watch_the_working_tree_recursively()
    {
        var roots = RepoWatchRoots.For(WorkingTree, GitDir, WatchCost.RecursiveIsOneWatchPerDirectory);

        Assert.DoesNotContain(roots, r => r.Path == WorkingTree && r.Recursive);
    }

    [Fact]
    public void Per_directory_platforms_watch_the_gitdir_itself_non_recursively()
    {
        var root = Root(GitDir);

        Assert.False(root.Recursive, "objects/ is the bulk of a gitdir and the classifier ignores all of it");
        Assert.Equal(WatchRootKind.GitDir, root.Kind);
        Assert.False(root.Optional, "the gitdir is the one root whose absence is worth reporting");
    }

    [Fact]
    public void Per_directory_platforms_watch_refs_recursively()
    {
        var root = Root(Path.Combine(GitDir, "refs"));

        Assert.True(root.Recursive, "a loose ref moving is a write inside refs/heads or refs/remotes");
        Assert.Equal(WatchRootKind.GitDir, root.Kind);
    }

    // `worktrees/` only exists once `git worktree add` has run, and `refs/` can be absent in a repo
    // that has none yet. Attaching to a missing directory throws, and that must not be reported as a
    // failure to watch.
    [Theory]
    [InlineData("refs")]
    [InlineData("worktrees")]
    public void Roots_git_creates_on_demand_are_optional(string name)
    {
        Assert.True(Root(Path.Combine(GitDir, name)).Optional);
    }

    [Fact]
    public void Per_directory_platforms_watch_the_worktrees_directory_recursively()
    {
        Assert.True(Root(Path.Combine(GitDir, "worktrees")).Recursive);
    }

    // modules/<name> is each submodule's entire gitdir, objects included — the exact cost this whole
    // layout exists to avoid. Submodules are their own registry entries and get their own roots; the
    // non-recursive gitdir root already reports modules/ itself appearing or vanishing.
    [Fact]
    public void The_modules_directory_is_never_a_root()
    {
        var roots = RepoWatchRoots.For(WorkingTree, GitDir, WatchCost.RecursiveIsOneWatchPerDirectory);

        Assert.DoesNotContain(roots, r => r.Path.Contains("modules", StringComparison.Ordinal));
    }

    [Fact]
    public void The_objects_directory_is_never_a_root()
    {
        var roots = RepoWatchRoots.For(WorkingTree, GitDir, WatchCost.RecursiveIsOneWatchPerDirectory);

        Assert.DoesNotContain(roots, r => r.Path.Contains("objects", StringComparison.Ordinal));
    }

    // One watch, and `.gitmodules` is the only thing on it we act on — an external edit to it is the
    // one working-tree signal with no polling equivalent. Its kind is what stops the other top-level
    // files it reports from becoming working-tree broadcasts.
    [Fact]
    public void The_working_tree_root_survives_as_a_single_non_recursive_gitmodules_watch()
    {
        var root = Root(WorkingTree);

        Assert.False(root.Recursive);
        Assert.Equal(WatchRootKind.Gitmodules, root.Kind);
    }

    // A worktree or submodule entry's gitdir lives under the *primary's* `.git`, nowhere near its own
    // working tree. Every gitdir-derived root has to follow the resolved path, or those entries watch
    // a directory that holds none of their refs.
    [Fact]
    public void Gitdir_roots_follow_the_resolved_gitdir_rather_than_the_working_tree()
    {
        var linked = Path.Combine(Src, "primary", ".git", "worktrees", "wt");

        var roots = RepoWatchRoots.For(WorkingTree, linked, WatchCost.RecursiveIsOneWatchPerDirectory);

        Assert.Contains(roots, r => r.Path == linked);
        Assert.Contains(roots, r => r.Path == Path.Combine(linked, "refs"));
        Assert.DoesNotContain(roots, r => r.Path == GitDir);
    }

    // The budget claim the plan rests on: a handful of roots per repo, whatever the repo looks like.
    [Fact]
    public void The_layout_stays_a_handful_of_roots()
    {
        var roots = RepoWatchRoots.For(WorkingTree, GitDir, WatchCost.RecursiveIsOneWatchPerDirectory);

        Assert.Equal(4, roots.Count);
        Assert.DoesNotContain(roots, r => r.Recursive && r.Kind != WatchRootKind.GitDir);
    }

    private static WatchRoot Root(string path)
    {
        var roots = RepoWatchRoots.For(WorkingTree, GitDir, WatchCost.RecursiveIsOneWatchPerDirectory);
        return Assert.Single(roots, r => r.Path == path);
    }
}
