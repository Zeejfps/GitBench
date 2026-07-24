using GitBench.Features.Repos;
using GitBench.Git;

namespace GitBench.Features.LocalChanges;

// Projects the active repo's local-changes slice out of the snapshot store into a plain snapshot
// value for the point-in-time dialogs (Discard, Stash), so they open on the same lists the panel
// behind them shows without re-running git status. A cold or failed slice degrades to an empty
// snapshot, so the dialogs fall to their empty state rather than blocking on a read the store owns.
internal static class LocalChangesProjection
{
    public static LocalChangesSnapshot ActiveSnapshot(IRepoSnapshotStore store, Repo repo)
        => store.LocalChanges.Value is Fetched<LocalChangesData>.Ok ok
            ? ok.Value.Snapshot
            : LocalChangesSnapshot.Empty(repo.Id);
}
