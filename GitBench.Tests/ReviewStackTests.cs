using System.Diagnostics;
using GitBench.Features.Commits;
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
        // the merge-base of main and feature, i.e. where feature diverged. The resolved base carries
        // the ref name + kind it came from, not just the SHA.
        var resolved = _git.ResolveAutoReviewBase(_repo, "feature");
        Assert.NotNull(resolved);
        Assert.Equal(c0, resolved!.Value.Sha);
        Assert.Equal("main", resolved.Value.Ref);
        Assert.Equal(ReviewBaseKind.DefaultBranch, resolved.Value.Kind);
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

    // Combined net diff (Phase 7.2): LoadRangeFiles is the whole base→head change as one file list,
    // and GetDiff(DiffSide.Range) is each file's net diff — both the *net* effect, not per-commit.

    [Fact]
    public void LoadRangeFiles_NetAcrossStack_DedupesTouchedFileAndNetsOutAddDelete()
    {
        var c0 = Commit("a.txt", "0", "base");   // a.txt present at base
        Commit("a.txt", "1", "modify a");        // touch a
        Commit("b.txt", "x", "add b");           // add b (absent in base)
        Commit("a.txt", "2", "modify a again");  // touch a again
        Git("rm", "b.txt");
        Git("commit", "-m", "remove b");
        var head = Head();

        var files = OkFiles(_git.LoadRangeFiles(_repo, c0, head));

        // a.txt is modified once net (0→2) despite two commits; b.txt was added then deleted, so the
        // net range doesn't list it at all.
        var f = Assert.Single(files);
        Assert.Equal("a.txt", f.Path);
        Assert.Equal(FileChangeStatus.Modified, f.Status);
    }

    [Fact]
    public void LoadRangeFiles_AddedFile_StatusAdded()
    {
        var c0 = Commit("a.txt", "0", "base");
        Commit("new.txt", "n", "add new");
        var head = Head();

        var files = OkFiles(_git.LoadRangeFiles(_repo, c0, head));

        // a.txt is unchanged base→head, so only the added file appears.
        var f = Assert.Single(files);
        Assert.Equal("new.txt", f.Path);
        Assert.Equal(FileChangeStatus.Added, f.Status);
    }

    [Fact]
    public void LoadRangeFiles_RenameAcrossRange_StatusRenamed()
    {
        var c0 = Commit("a.txt", "the quick brown fox jumps over the lazy dog\n", "base");
        Git("mv", "a.txt", "b.txt");
        Git("commit", "-m", "rename a to b");
        var head = Head();

        var files = OkFiles(_git.LoadRangeFiles(_repo, c0, head));

        var f = Assert.Single(files);
        Assert.Equal(FileChangeStatus.Renamed, f.Status);
        Assert.Equal("b.txt", f.Path);
        Assert.Equal("a.txt", f.OldPath);
    }

    [Fact]
    public void LoadRangeFiles_BaseEqualsHead_IsEmpty()
    {
        var c0 = Commit("a.txt", "0", "base");
        Commit("a.txt", "1", "ahead");

        var files = OkFiles(_git.LoadRangeFiles(_repo, c0, c0));

        Assert.Empty(files);
    }

    [Fact]
    public void GetDiff_Range_IsNetBaseToHead_NotPerCommit()
    {
        var c0 = Commit("a.txt", "v0\n", "base");
        Commit("a.txt", "v1\n", "mod1");
        Commit("a.txt", "v2\n", "mod2");
        var head = Head();

        var diff = _git.GetDiff(_repo, "a.txt", DiffSide.Range, commitSha: head, baseSha: c0);

        Assert.Null(diff.ErrorMessage);
        Assert.False(diff.IsBinary);
        var removed = diff.Hunks.SelectMany(h => h.Lines)
            .Where(l => l.Kind == DiffLineKind.Removed).Select(l => l.Text.Trim()).ToList();
        var added = diff.Hunks.SelectMany(h => h.Lines)
            .Where(l => l.Kind == DiffLineKind.Added).Select(l => l.Text.Trim()).ToList();

        // The net base→head removes the BASE line (v0) and adds the HEAD line (v2); the intermediate
        // v1 appears on neither side — proof this is base→head, not the tip commit's own diff (which
        // would remove v1).
        Assert.Contains("v0", removed);
        Assert.Contains("v2", added);
        Assert.DoesNotContain("v1", removed);
        Assert.DoesNotContain("v1", added);
    }

    private static IReadOnlyList<FileChange> OkFiles(Fetched<IReadOnlyList<FileChange>> fetched)
        => Assert.IsType<Fetched<IReadOnlyList<FileChange>>.Ok>(fetched).Value;

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
