using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// §10: the cold-store gap is resolved in exactly one place — the shared projection helper. An Ok
// slice projects to its snapshot; a null (cross-repo switch / retry) or Failed slice degrades to
// Empty(repo.Id), which feeds the dialogs' empty state instead of blocking.
public sealed class LocalChangesProjectionTests
{
    private sealed class FakeSnapshotStore : IRepoSnapshotStore
    {
        public State<Fetched<LocalChangesData>?> LocalState { get; } = new(null);
        public IReadable<Fetched<CommitSnapshot>?> Commits { get; } = new State<Fetched<CommitSnapshot>?>(null);
        public IReadable<Fetched<BranchListing>?> Branches { get; } = new State<Fetched<BranchListing>?>(null);
        public IReadable<Fetched<LocalChangesData>?> LocalChanges => LocalState;
    }

    private static readonly Repo Repo = new(Guid.NewGuid(), "/tmp/repo", "test");

    [Fact]
    public void Ok_slice_projects_to_its_snapshot()
    {
        var snapshot = new LocalChangesSnapshot(
            Repo.Id,
            Staged: Array.Empty<FileChange>(),
            Unstaged: new[] { new FileChange("a.txt", null, FileChangeStatus.Modified) },
            GitStatusSummary.Unknown);
        var store = new FakeSnapshotStore
        {
            LocalState = { Value = new Fetched<LocalChangesData>.Ok(new LocalChangesData(snapshot, Array.Empty<GitBench.Features.Submodules.SubmoduleInfo>())) },
        };

        Assert.Same(snapshot, LocalChangesProjection.ActiveSnapshot(store, Repo));
    }

    [Fact]
    public void Null_slice_degrades_to_empty()
    {
        var store = new FakeSnapshotStore(); // LocalChanges.Value == null

        var projected = LocalChangesProjection.ActiveSnapshot(store, Repo);

        Assert.Equal(Repo.Id, projected.RepoId);
        Assert.Empty(projected.Staged);
        Assert.Empty(projected.Unstaged);
    }

    [Fact]
    public void Failed_slice_degrades_to_empty()
    {
        var store = new FakeSnapshotStore
        {
            LocalState = { Value = new Fetched<LocalChangesData>.Failed("boom") },
        };

        var projected = LocalChangesProjection.ActiveSnapshot(store, Repo);

        Assert.Equal(Repo.Id, projected.RepoId);
        Assert.Empty(projected.Staged);
        Assert.Empty(projected.Unstaged);
    }
}
