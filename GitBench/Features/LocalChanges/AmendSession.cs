using GitBench.Features.Commits;
using GitBench.Git;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// Captures the extra state that exists only while the user is amending the previous
/// commit: a backup of the editor's title/description so toggling amend off can restore
/// them, the HEAD commit's message used to populate the editor on entry, and the staged
/// file list diffed against HEAD's parent — the contents the amended commit would record,
/// which is what the staged panel shows in amend mode. Owned by
/// <see cref="LocalChangesViewModel"/>; mutated only on the UI thread.
/// </summary>
internal sealed class AmendSession
{
    public string PreAmendTitle { get; }
    public string PreAmendDescription { get; }
    public string Title { get; }
    public string Description { get; }
    public IReadOnlyList<FileChange> StagedFiles { get; private set; }

    private AmendSession(
        string preAmendTitle,
        string preAmendDescription,
        string title,
        string description,
        IReadOnlyList<FileChange> stagedFiles)
    {
        PreAmendTitle = preAmendTitle;
        PreAmendDescription = preAmendDescription;
        Title = title;
        Description = description;
        StagedFiles = stagedFiles;
    }

    /// <summary>
    /// Builds a session from the pre-amend editor backups, HEAD's message (may be null when there
    /// is no commit to amend), and a seed staged list. Makes no git calls — the caller has already
    /// read what it needs; the seed is a stand-in that the amend diff replaces once it lands.
    /// </summary>
    public static AmendSession Begin(
        string preAmendTitle,
        string preAmendDescription,
        HeadCommitMessage? head,
        IReadOnlyList<FileChange> seedStagedFiles)
        => new(
            preAmendTitle,
            preAmendDescription,
            head?.Title ?? string.Empty,
            head?.Description ?? string.Empty,
            seedStagedFiles);

    /// <summary>Replaces the staged-vs-parent file list. Called from the VM's load path
    /// on every reload so the displayed staged panel tracks index mutations and external
    /// HEAD moves made while amending.</summary>
    public void UpdateStagedFiles(IReadOnlyList<FileChange> stagedFiles)
        => StagedFiles = stagedFiles;

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
