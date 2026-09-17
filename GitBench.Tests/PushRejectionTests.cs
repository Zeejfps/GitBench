using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

// The real GitService against a throwaway bare origin: a push refused because someone else pushed
// first must come back as Rejected (the pull-then-push dialog's cue), and the recovery that dialog
// runs — pull with a strategy, then push — must land.
public sealed class PushRejectionTests : IDisposable
{
    private readonly string _work;
    private readonly string _origin;
    private readonly GitService _git;
    private readonly Repo _repo;

    public PushRejectionTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "gitbench-push-rejected-" + Guid.NewGuid().ToString("N"));
        _work = Path.Combine(root, "work");
        _origin = Path.Combine(root, "origin.git");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_origin);

        TestGit.Run(_origin, "init", "--bare", "-b", "main");
        TestGit.Init(_work);
        Git("remote", "add", "origin", _origin.Replace('\\', '/'));
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");

        _git = new GitService(new RepoActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _work, "test");
    }

    [Fact]
    public void Push_behind_a_teammate_is_reported_as_rejected_not_failed()
    {
        AdvanceOriginByOneCommit();
        Commit("c.txt", "1", "local work");

        var outcome = _git.Push(_repo);

        var rejected = Assert.IsType<PushOutcome.Rejected>(outcome);
        Assert.Contains("rejected", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pull_with_rebase_then_push_lands_after_a_rejection()
    {
        AdvanceOriginByOneCommit();
        Commit("c.txt", "1", "local work");
        Assert.IsType<PushOutcome.Rejected>(_git.Push(_repo));

        Assert.IsType<PullOutcome.Completed>(_git.Pull(_repo, PullStrategy.Rebase));
        Assert.IsType<PushOutcome.Completed>(_git.Push(_repo));

        var originTip = TestGit.Run(_origin, "log", "-1", "--format=%s", "main").Trim();
        Assert.Equal("local work", originTip);
    }

    [Fact]
    public void Push_with_nothing_behind_is_not_rejected()
    {
        Commit("c.txt", "1", "local work");

        Assert.IsType<PushOutcome.Completed>(_git.Push(_repo));
    }

    private void AdvanceOriginByOneCommit()
    {
        var parent = Path.GetDirectoryName(_work)!;
        var clone = Path.Combine(parent, "clone");
        TestGit.Run(parent, "clone", _origin.Replace('\\', '/'), clone);
        TestGit.Run(clone, "config", "user.name", "Test");
        TestGit.Run(clone, "config", "user.email", "test@example.com");
        TestGit.Run(clone, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(clone, "remote-change.txt"), "r");
        TestGit.Run(clone, "add", "remote-change.txt");
        TestGit.Run(clone, "commit", "-m", "remote work");
        TestGit.Run(clone, "push", "origin", "main");
    }

    private void Commit(string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(_work, file), content);
        Git("add", file);
        Git("commit", "-m", message);
    }

    private void Git(params string[] args) => TestGit.Run(_work, args);

    public void Dispose()
    {
        DirectoryTree.Delete(Path.GetDirectoryName(_work)!);
    }
}
