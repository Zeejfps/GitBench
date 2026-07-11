namespace GitBench.Features.LocalChanges;

/// <summary>
/// Whose working tree the Changes tab's Review layout shows (Phase 5.3). A session-scoped presentation
/// choice — <em>not</em> persisted — offered only while the active repo's checked-out branch is a synced
/// change-set branch with two or more members on it. Switching away from such a branch falls back to
/// <see cref="ThisRepo"/> for free (the cross-repo surface reports itself unavailable and the panel
/// renders the single-repo review).
/// </summary>
public enum ChangeSetPanelScope
{
    /// <summary>Just the active repo's working tree — the ordinary single-repo review.</summary>
    ThisRepo,

    /// <summary>Every member of the change set that has the branch checked out, aggregated into one
    /// review surface: check a file to stage it in its own repo, commit the whole set with one message.</summary>
    AllRepos,
}
