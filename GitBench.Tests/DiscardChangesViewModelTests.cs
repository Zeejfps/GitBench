using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// §10: the Discard dialog VM is a pure function of the LocalChangesSnapshot it is handed — it must
// never read git in its constructor. A throwing-on-read GitService proves the read is gone.
public sealed class DiscardChangesViewModelTests
{
    private static readonly Repo Repo = new(Guid.NewGuid(), "/tmp/repo", "test");

    private static FileChange F(string path, FileChangeStatus status = FileChangeStatus.Modified)
        => new(path, null, status);

    private static (CountingGitService Git, LocalizationService Loc, QueuedDispatcher Dispatcher) Env()
    {
        var git = new CountingGitService(new GitService(new RepoActivityTracker())) { ThrowOnReads = true };
        return (git, new LocalizationService(new State<Locale>(Locale.En)), new QueuedDispatcher());
    }

    [Fact]
    public void Constructor_reads_no_git_and_lists_the_snapshot_unstaged_sorted()
    {
        var (git, loc, dispatcher) = Env();
        var snapshot = new LocalChangesSnapshot(
            Repo.Id,
            Staged: new[] { F("staged-only.txt") },
            Unstaged: new[] { F("b.txt"), F("a.txt", FileChangeStatus.Added) },
            GitStatusSummary.Unknown);

        var vm = new DiscardChangesViewModel(
            new DiscardChangesRequest(Repo, Array.Empty<string>()),
            snapshot, git, dispatcher, new MessageBus(), loc);

        // No read fired (ThrowOnReads would have thrown; the counters stay zero).
        Assert.Equal(0, git.GetLocalChangesCalls);
        Assert.Equal(0, git.GetHeadCommitMessageCalls);

        // Discard lists only the Unstaged side, sorted, ignoring Staged.
        Assert.Equal(new[] { "a.txt", "b.txt" }, vm.Files.Value.Select(r => r.Path).ToArray());
    }

    [Fact]
    public void Empty_snapshot_yields_no_rows_and_the_empty_state_header()
    {
        var (git, loc, dispatcher) = Env();

        var vm = new DiscardChangesViewModel(
            new DiscardChangesRequest(Repo, Array.Empty<string>()),
            LocalChangesSnapshot.Empty(Repo.Id), git, dispatcher, new MessageBus(), loc);

        Assert.Equal(0, git.GetLocalChangesCalls);
        Assert.Empty(vm.Files.Value);
        Assert.Equal(loc.Strings.Value.LocalchangesFilesHeaderEmpty, vm.FilesHeader.Value);
    }
}
