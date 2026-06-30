using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// Range backend (Phase 4): GitService.LoadReviewStack lists base..head as a first-parent stack
// oldest→newest, and MergeBase/ResolveAutoReviewBase anchor the base. Each test builds a fresh
// throwaway repo on disk via the git CLI, then drives the real GitService against it.
public sealed class ReviewStackTests : IDisposable
{
    private readonly string _root;
    private readonly GitService _git;
    private readonly Repo _repo;

    public ReviewStackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Git("init", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
        _git = new GitService(new RepoActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _root, "test");
    }

    [Fact]
    public void LoadReviewStack_LinearStack_ReturnsIncrementsOldestToNewest()
    {
        var c0 = Commit("a.txt", "0", "base");
        var c1 = Commit("a.txt", "1", "first");
        var c2 = Commit("a.txt", "2", "second");
        var c3 = Commit("a.txt", "3", "third");

        var stack = Ok(_git.LoadReviewStack(_repo, c0, "main", 100));

        Assert.Equal(new[] { c1, c2, c3 }, stack.Increments.Select(i => i.Sha));
        Assert.Equal(new[] { "first", "second", "third" }, stack.Increments.Select(i => i.Summary));
        Assert.Equal(c0, stack.BaseSha);
        Assert.Equal(c3, stack.HeadSha);
        Assert.False(stack.Truncated);
    }

    [Fact]
    public void LoadReviewStack_SingleCommit_ReturnsOneIncrement()
    {
        var c0 = Commit("a.txt", "0", "base");
        var c1 = Commit("a.txt", "1", "only");

        var stack = Ok(_git.LoadReviewStack(_repo, c0, c1, 100));

        var inc = Assert.Single(stack.Increments);
        Assert.Equal(c1, inc.Sha);
        Assert.Equal("only", inc.Summary);
    }

    [Fact]
    public void LoadReviewStack_BaseEqualsHead_IsEmpty()
    {
        var c0 = Commit("a.txt", "0", "base");
        Commit("a.txt", "1", "ahead");

        var stack = Ok(_git.LoadReviewStack(_repo, c0, c0, 100));

        Assert.Empty(stack.Increments);
        Assert.False(stack.Truncated);
    }

    [Fact]
    public void LoadReviewStack_CapExceeded_TruncatesToNewest()
    {
        var c0 = Commit("a.txt", "0", "base");
        Commit("a.txt", "1", "first");
        var c2 = Commit("a.txt", "2", "second");
        var c3 = Commit("a.txt", "3", "third");

        var stack = Ok(_git.LoadReviewStack(_repo, c0, "main", 2));

        // The walk takes the newest `cap`; reversed it keeps the two tip-most increments.
        Assert.Equal(new[] { c2, c3 }, stack.Increments.Select(i => i.Sha));
        Assert.True(stack.Truncated);
    }

    [Fact]
    public void LoadReviewStack_MergeCommit_FollowsFirstParentOnly()
    {
        var c0 = Commit("a.txt", "0", "base");
        Git("checkout", "-b", "feature");
        Commit("f.txt", "1", "feature-1");
        Commit("f.txt", "2", "feature-2");
        Git("checkout", "main");
        var m1 = Commit("m.txt", "1", "main-1");
        Git("merge", "--no-ff", "feature", "-m", "merge feature");
        var merge = Head();

        var stack = Ok(_git.LoadReviewStack(_repo, c0, "main", 100));

        // First-parent linearization: only the mainline (main-1, merge), never the merged side.
        Assert.Equal(new[] { m1, merge }, stack.Increments.Select(i => i.Sha));
        Assert.DoesNotContain(stack.Increments, i => i.Summary.StartsWith("feature-"));
    }

    [Fact]
    public void LoadReviewStack_BadRef_Failed()
    {
        Commit("a.txt", "0", "base");

        var result = _git.LoadReviewStack(_repo, "no-such-ref", "main", 100);

        Assert.IsType<Fetched<ReviewStack>.Failed>(result);
    }

    [Fact]
    public void MergeBase_DivergedBranches_ReturnsCommonAncestor()
    {
        var c0 = Commit("a.txt", "0", "base");
        Git("checkout", "-b", "feature");
        Commit("f.txt", "1", "feature-1");
        Git("checkout", "main");
        Commit("m.txt", "1", "main-1");

        Assert.Equal(c0, _git.MergeBase(_repo, "main", "feature"));
    }

    [Fact]
    public void MergeBase_BadRef_ReturnsNull()
    {
        Commit("a.txt", "0", "base");

        Assert.Null(_git.MergeBase(_repo, "main", "no-such-ref"));
    }

    [Fact]
    public void MergeBase_UnrelatedHistories_ReturnsNull()
    {
        Commit("a.txt", "0", "base");
        // An orphan branch shares no history with main.
        Git("checkout", "--orphan", "orphan");
        Commit("b.txt", "0", "orphan-base");

        Assert.Null(_git.MergeBase(_repo, "main", "orphan"));
    }

    [Fact]
    public void ResolveAutoReviewBase_NoUpstream_FallsBackToDefaultBranch()
    {
        var c0 = Commit("a.txt", "0", "base");
        Git("checkout", "-b", "feature");
        Commit("f.txt", "1", "feature-1");
        Commit("f.txt", "2", "feature-2");

        // No upstream configured ⇒ falls back to the local default branch (main); the auto base is
        // the merge-base of main and feature, i.e. where feature diverged.
        Assert.Equal(c0, _git.ResolveAutoReviewBase(_repo, "feature"));
    }

    [Fact]
    public void ResolveAutoReviewBase_NoDefaultBranch_ReturnsNull()
    {
        // Only a non-default branch exists: switching off the unborn main before the first commit
        // leaves no main/master ref (and there's no upstream or origin/HEAD).
        Git("checkout", "-b", "solo");
        Commit("a.txt", "0", "base");

        Assert.Null(_git.ResolveAutoReviewBase(_repo, "solo"));
    }

    private static ReviewStack Ok(Fetched<ReviewStack> fetched)
        => Assert.IsType<Fetched<ReviewStack>.Ok>(fetched).Value;

    private string Commit(string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(_root, file), content);
        Git("add", file);
        Git("commit", "-m", message);
        return Head();
    }

    private string Head() => Git("rev-parse", "HEAD").Trim();

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");
        return stdout;
    }

    public void Dispose()
    {
        try { ForceDelete(new DirectoryInfo(_root)); }
        catch { /* best effort: a leftover temp repo is harmless */ }
    }

    // git marks loose objects read-only, which trips Directory.Delete on Windows; clear attributes
    // depth-first before removing.
    private static void ForceDelete(DirectoryInfo dir)
    {
        if (!dir.Exists) return;
        foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        dir.Delete(recursive: true);
    }
}
