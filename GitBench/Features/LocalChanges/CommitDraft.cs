namespace GitBench.Features.LocalChanges;

/// <summary>
/// A repository's unsent commit message. The commit box belongs to the repository rather than to the
/// window, so what was typed is parked under the repo it was typed in and comes back on return.
/// </summary>
internal readonly record struct CommitDraft(string Title, string Description)
{
    public static readonly CommitDraft Empty = new(string.Empty, string.Empty);

    public bool IsEmpty => Title.Length == 0 && Description.Length == 0;
}
