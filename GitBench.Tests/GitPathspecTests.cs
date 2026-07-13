using System.Diagnostics;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// Bulk path operations must survive path lists whose combined length exceeds the Windows
// 32,767-char CreateProcess command-line cap. The integration tests drive GitService
// against a real temp repo with a path list well past that cap (paths travel via
// --pathspec-from-file on stdin, or chunked command lines on old git).
public sealed class GitPathspecTests : IDisposable
{
    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    private readonly string _root;
    private readonly GitService _git;
    private readonly Repo _repo;

    public GitPathspecTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-pathspec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _git = new GitService(new NullActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _root, "test");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    private LocalChangesSnapshot Snapshot()
    {
        var fetched = _git.GetLocalChanges(_repo);
        return Assert.IsType<Fetched<LocalChangesSnapshot>.Ok>(fetched).Value;
    }

    private List<string> CreateManyFiles()
    {
        // ~400 files x ~100-char paths ≈ 40k chars of pathspec — past the Windows cap.
        var dir = Path.Combine(_root, "some", "fairly", "deeply", "nested", "directory", "structure");
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        for (var i = 0; i < 400; i++)
        {
            var name = $"file-{i:D4}-{new string('x', 40)}.txt";
            File.WriteAllText(Path.Combine(dir, name), $"content {i}\n");
            paths.Add($"some/fairly/deeply/nested/directory/structure/{name}");
        }
        // Non-ASCII path exercises the UTF-8 stdin encoding of the pathspec list.
        File.WriteAllText(Path.Combine(_root, "файл-日本語.txt"), "unicode\n");
        paths.Add("файл-日本語.txt");
        return paths;
    }

    [Fact]
    public void StageUnstageDiscard_SurviveHugePathList()
    {
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");
        // `git restore --staged` resolves HEAD; seed a commit so it exists.
        File.WriteAllText(Path.Combine(_root, "seed.txt"), "seed\n");
        Git("add", "seed.txt");
        Git("-c", "commit.gpgsign=false", "commit", "-m", "seed");
        var paths = CreateManyFiles();

        var staged = _git.Stage(_repo, paths);
        Assert.True(staged is GitOutcome.Success, staged.FailureMessage);
        Assert.Equal(paths.Count, Snapshot().Staged.Count);

        var unstaged = _git.Unstage(_repo, paths);
        Assert.True(unstaged is GitOutcome.Success, unstaged.FailureMessage);
        Assert.Empty(Snapshot().Staged);

        // Untracked discard = delete from disk; every file above is untracked here.
        var discarded = _git.DiscardChanges(_repo, paths);
        Assert.True(discarded is GitOutcome.Success, discarded.FailureMessage);
        Assert.Empty(Snapshot().Unstaged);
    }

    [Fact]
    public void ChunkPaths_SmallListStaysInOneBatch()
    {
        var paths = new[] { "a.txt", "b.txt", "c.txt" };
        var batches = GitService.ChunkPathsForCommandLine(paths).ToList();
        Assert.Single(batches);
        Assert.Equal(paths, batches[0]);
    }

    [Fact]
    public void ChunkPaths_SplitsAtBudgetAndPreservesOrder()
    {
        var paths = Enumerable.Range(0, 500).Select(i => $"dir/{i:D4}-{new string('x', 95)}").ToList();
        var batches = GitService.ChunkPathsForCommandLine(paths).ToList();

        Assert.True(batches.Count > 1);
        Assert.Equal(paths, batches.SelectMany(b => b));
        foreach (var batch in batches)
            Assert.True(batch.Sum(p => p.Length + 3) <= GitService.PathspecCommandLineBudget);
    }

    [Fact]
    public void ChunkPaths_PathLongerThanBudgetStillYields()
    {
        var huge = new string('x', GitService.PathspecCommandLineBudget + 100);
        var batches = GitService.ChunkPathsForCommandLine(new[] { "a.txt", huge, "b.txt" }).ToList();

        Assert.Equal(new[] { "a.txt", huge, "b.txt" }, batches.SelectMany(b => b));
    }
}
