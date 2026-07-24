using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Stash;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// §10: the Stash dialog VM builds from the handed LocalChangesSnapshot and never reads git in its
// constructor. It merges Staged + Unstaged and derives the untracked flag from Status == Added.
public sealed class StashDialogViewModelTests
{
    private static readonly Repo Repo = new(Guid.NewGuid(), "/tmp/repo", "test");

    private static FileChange F(string path, FileChangeStatus status = FileChangeStatus.Modified)
        => new(path, null, status);

    private static (CountingGitService Git, LocalizationService Loc, QueuedDispatcher Dispatcher) Env()
    {
        var git = new CountingGitService(new GitService(new RepoActivityTracker())) { ThrowOnReads = true };
        return (git, new LocalizationService(new State<Locale>(Locale.En)), new QueuedDispatcher());
    }

    private static StashDialogViewModel Build(
        LocalChangesSnapshot snapshot, CountingGitService git, LocalizationService loc, QueuedDispatcher dispatcher)
        => new(
            new StashRequest(Repo), snapshot, git, dispatcher, new MessageBus(),
            new LocalChangesSelectionStore(), loc);

    [Fact]
    public void Constructor_reads_no_git_and_merges_staged_and_unstaged()
    {
        var (git, loc, dispatcher) = Env();
        var snapshot = new LocalChangesSnapshot(
            Repo.Id,
            Staged: new[] { F("staged.txt") },
            Unstaged: new[] { F("mod.txt"), F("newfile.txt", FileChangeStatus.Added) },
            GitStatusSummary.Unknown);

        var vm = Build(snapshot, git, loc, dispatcher);

        Assert.Equal(0, git.GetLocalChangesCalls);

        var rows = vm.Files.Value;
        Assert.Equal(new[] { "mod.txt", "newfile.txt", "staged.txt" }, rows.Select(r => r.Path).ToArray());

        // The untracked flag is set exactly for the Status == Added row.
        Assert.True(rows.Single(r => r.Path == "newfile.txt").IsUntracked);
        Assert.False(rows.Single(r => r.Path == "mod.txt").IsUntracked);
        Assert.False(rows.Single(r => r.Path == "staged.txt").IsUntracked);
    }

    [Fact]
    public void Empty_snapshot_yields_no_rows_and_the_empty_state_header()
    {
        var (git, loc, dispatcher) = Env();

        var vm = Build(LocalChangesSnapshot.Empty(Repo.Id), git, loc, dispatcher);

        Assert.Equal(0, git.GetLocalChangesCalls);
        Assert.Empty(vm.Files.Value);
        Assert.Equal(loc.Strings.Value.LocalchangesFilesHeaderEmpty, vm.FilesHeader.Value);
    }
}
