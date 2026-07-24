using System.Diagnostics;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// Replays the LocalChangesViewModel amend-unstage flow against a real temp repo:
// commit a file, enter amend, unstage that file, then recompute the displayed
// staged list the way the VM's reload path does.
public sealed class AmendUnstageTests : IDisposable
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

    public AmendUnstageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-amend-" + Guid.NewGuid().ToString("N"));
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
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    private void WriteFile(string name, string content)
        => File.WriteAllText(Path.Combine(_root, name), content);

    private LocalChangesSnapshot Snapshot()
    {
        var fetched = _git.GetLocalChanges(_repo);
        return Assert.IsType<Fetched<LocalChangesSnapshot>.Ok>(fetched).Value;
    }

    [Fact]
    public void UnstagingHeadFileDuringAmend_LeavesStagedAndPopulatesUnstaged()
    {
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");
        WriteFile("a.txt", "one\n");
        Git("add", "a.txt");
        Git("commit", "-m", "base");
        WriteFile("a.txt", "two\n");
        Git("add", "a.txt");
        Git("commit", "-m", "second");

        // Click Amend: the VM reads HEAD's message and the staged-vs-parent list, then seeds the
        // session with them (Begin makes no git calls of its own).
        var head = _git.GetHeadCommitMessage(_repo);
        var session = AmendSession.Begin("", "", head, _git.GetAmendStagedFiles(_repo));
        var stagedFromIndex = Snapshot().Staged;
        Assert.Contains(session.StagedFiles, f => f.Path == "a.txt");

        // Unstage a.txt: the VM classifies it as HEAD-only and resets it against HEAD^.
        var (toUnstage, toReset) = session.Classify(new[] { "a.txt" }, stagedFromIndex);
        Assert.True(_git.Unstage(_repo, toUnstage) is not GitOutcome.Failed);
        Assert.True(_git.ResetToParent(_repo, toReset) is not GitOutcome.Failed);

        // Reload path: recompute the staged-vs-parent list.
        session.UpdateStagedFiles(_git.GetAmendStagedFiles(_repo));
        var snap = Snapshot();

        Assert.DoesNotContain(session.StagedFiles, f => f.Path == "a.txt");
        Assert.Contains(snap.Unstaged, f => f.Path == "a.txt");
    }

    [Fact]
    public void UnstagingFileStagedDuringAmend_TakesNormalUnstagePath()
    {
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");
        WriteFile("a.txt", "one\n");
        Git("add", "a.txt");
        Git("commit", "-m", "base");
        WriteFile("a.txt", "two\n");
        Git("add", "a.txt");
        Git("commit", "-m", "second");

        var head = _git.GetHeadCommitMessage(_repo);
        var session = AmendSession.Begin("", "", head, _git.GetAmendStagedFiles(_repo));

        // Stage a brand-new file while amending; it appears in the staged panel.
        WriteFile("b.txt", "new\n");
        Assert.True(_git.Stage(_repo, new[] { "b.txt" }) is not GitOutcome.Failed);
        session.UpdateStagedFiles(_git.GetAmendStagedFiles(_repo));
        Assert.Contains(session.StagedFiles, f => f.Path == "b.txt");

        // Unstage it: it's index-staged, so it routes through the normal unstage batch.
        var stagedFromIndex = Snapshot().Staged;
        var (toUnstage, toReset) = session.Classify(new[] { "b.txt" }, stagedFromIndex);
        Assert.Equal(new[] { "b.txt" }, toUnstage);
        Assert.Empty(toReset);
        Assert.True(_git.Unstage(_repo, toUnstage) is not GitOutcome.Failed);

        session.UpdateStagedFiles(_git.GetAmendStagedFiles(_repo));
        var snap = Snapshot();

        Assert.DoesNotContain(session.StagedFiles, f => f.Path == "b.txt");
        Assert.Contains(snap.Unstaged, f => f.Path == "b.txt");
        // The HEAD-carried change is untouched by the b.txt unstage.
        Assert.Contains(session.StagedFiles, f => f.Path == "a.txt");
    }

    // §10: AmendSession.Begin is now a pure value factory — it takes the already-read head message
    // and seed list and makes no git calls, so no IGitService is even in scope here.
    [Fact]
    public void Begin_is_a_pure_value_factory_over_head_message_and_seed()
    {
        var head = new HeadCommitMessage("subject", "body");
        var seed = new FileChange[] { new("x.txt", null, FileChangeStatus.Modified) };

        var session = AmendSession.Begin("preT", "preD", head, seed);

        Assert.Equal("preT", session.PreAmendTitle);
        Assert.Equal("preD", session.PreAmendDescription);
        Assert.Equal("subject", session.Title);
        Assert.Equal("body", session.Description);
        Assert.Same(seed, session.StagedFiles);
    }

    [Fact]
    public void Begin_with_a_null_head_carries_empty_editor_text()
    {
        var session = AmendSession.Begin("preT", "preD", null, Array.Empty<FileChange>());

        Assert.Equal(string.Empty, session.Title);
        Assert.Equal(string.Empty, session.Description);
        Assert.Empty(session.StagedFiles);
    }
}
