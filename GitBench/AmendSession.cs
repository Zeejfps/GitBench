namespace GitGui;

/// <summary>
/// Captures the extra state that exists only while the user is amending the previous
/// commit: a backup of the editor's title/description so toggling amend off can restore
/// them, the HEAD commit's message used to populate the editor on entry, and HEAD's file
/// list so the staged panel can surface files that would otherwise silently carry over
/// into the amended commit. Owned by <see cref="LocalChangesViewModel"/>; mutated only on
/// the UI thread.
/// </summary>
internal sealed class AmendSession
{
    public string PreAmendTitle { get; }
    public string PreAmendDescription { get; }
    public string Title { get; }
    public string Description { get; }
    public IReadOnlyList<FileChange> HeadFiles { get; private set; }

    private AmendSession(
        string preAmendTitle,
        string preAmendDescription,
        string title,
        string description,
        IReadOnlyList<FileChange> headFiles)
    {
        PreAmendTitle = preAmendTitle;
        PreAmendDescription = preAmendDescription;
        Title = title;
        Description = description;
        HeadFiles = headFiles;
    }

    /// <summary>
    /// Builds a session by snapshotting HEAD's message and file list. <paramref name="repo"/>
    /// may be null — the VM defensively allows entering amend mode with no active repo,
    /// in which case the session carries empty HEAD data but still remembers the editor
    /// backups so toggling amend off restores them.
    /// </summary>
    public static AmendSession Begin(
        IGitService gitService,
        Repo? repo,
        string preAmendTitle,
        string preAmendDescription)
    {
        if (repo == null)
        {
            return new AmendSession(
                preAmendTitle, preAmendDescription,
                string.Empty, string.Empty,
                Array.Empty<FileChange>());
        }

        var head = gitService.GetHeadCommitMessage(repo);
        var headFiles = gitService.GetHeadCommitFiles(repo);
        return new AmendSession(
            preAmendTitle,
            preAmendDescription,
            head?.Title ?? string.Empty,
            head?.Description ?? string.Empty,
            headFiles);
    }

    /// <summary>Replaces the captured HEAD file list. Called from the VM's load path
    /// after a refs-changed reload so the displayed staged panel doesn't keep showing
    /// files from a HEAD that has since moved.</summary>
    public void UpdateHeadFiles(IReadOnlyList<FileChange> headFiles)
        => HeadFiles = headFiles;

    /// <summary>
    /// Merges HEAD's files into the staged-from-index list so the user can see — and
    /// optionally drop — files that would carry over into the amended commit. On path
    /// collision the index entry wins so the badge reflects the *current* change rather
    /// than the previous-commit change.
    /// </summary>
    public IReadOnlyList<FileChange> MergeWithIndex(IReadOnlyList<FileChange> stagedFromIndex)
    {
        if (HeadFiles.Count == 0) return stagedFromIndex;

        var seen = new HashSet<string>(stagedFromIndex.Select(f => f.Path));
        var merged = new List<FileChange>(stagedFromIndex.Count + HeadFiles.Count);
        merged.AddRange(stagedFromIndex);
        foreach (var h in HeadFiles)
        {
            if (seen.Add(h.Path))
                merged.Add(h);
        }
        merged.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return merged;
    }

    /// <summary>
    /// Splits a user-supplied path list into two batches: paths that exist in the index
    /// (normal unstage) and HEAD-only paths that must be reset against HEAD~1 to drop
    /// them from the amended commit.
    /// </summary>
    public (IReadOnlyList<string> ToUnstage, IReadOnlyList<string> ToResetToParent) Classify(
        IReadOnlyList<string> paths,
        IReadOnlyList<FileChange> stagedFromIndex)
    {
        var stagedPaths = new HashSet<string>(stagedFromIndex.Select(f => f.Path));
        List<string>? toUnstage = null;
        List<string>? toReset = null;
        foreach (var p in paths)
        {
            if (stagedPaths.Contains(p))
                (toUnstage ??= new List<string>()).Add(p);
            else
                (toReset ??= new List<string>()).Add(p);
        }
        return (
            toUnstage ?? (IReadOnlyList<string>)Array.Empty<string>(),
            toReset ?? (IReadOnlyList<string>)Array.Empty<string>());
    }
}
