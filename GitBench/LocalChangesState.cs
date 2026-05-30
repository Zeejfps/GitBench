namespace GitGui;

internal readonly record struct LocalChangesState(
    string Title,
    string Description,
    bool Amend,
    bool HasRepo,
    bool IsLoading,
    string? LoadError,
    IReadOnlyList<FileChange> Unstaged,
    IReadOnlyList<FileChange> Staged,
    Selection Selection,
    string? OpError,
    bool CommitBusy,
    // Submodules whose current HEAD differs from the parent's recorded pointer. Empty
    // when nothing is drifted. Shown in a dedicated section above the file panels.
    IReadOnlyList<SubmoduleInfo> DriftedSubmodules,
    // Flat list vs. directory tree. Persisted globally via PreferencesService.
    FileViewMode ViewMode,
    // Folder full-paths currently collapsed in tree mode, per side. In-memory only
    // (resets on repo switch / relaunch). Missing key ⇒ expanded.
    IReadOnlySet<string> UnstagedCollapsed,
    IReadOnlySet<string> StagedCollapsed)
{
    public const string OpenRepoPlaceholder = "Open a repository to see local changes.";
    public const string LoadingPlaceholder = "Loading…";

    private static readonly IReadOnlySet<string> EmptyCollapsed = new HashSet<string>();

    public static LocalChangesState Initial { get; } = new(
        Title: string.Empty,
        Description: string.Empty,
        Amend: false,
        HasRepo: false,
        IsLoading: false,
        LoadError: null,
        Unstaged: [],
        Staged: [],
        Selection: Selection.Empty,
        OpError: null,
        CommitBusy: false,
        DriftedSubmodules: [],
        ViewMode: FileViewMode.Flat,
        UnstagedCollapsed: EmptyCollapsed,
        StagedCollapsed: EmptyCollapsed);

    // Placeholder is derived, not settable. Loading never tears the panels down when
    // there is data on screen — that's reserved for "nothing to render at all"
    // (no repo, hard load error, or a cold start with empty lists while loading).
    // Splitting lifecycle (IsLoading / LoadError / HasRepo) from data (Staged / Unstaged)
    // makes the "Loading shown while data exists" state unrepresentable.
    public string? Placeholder =>
        !HasRepo ? OpenRepoPlaceholder :
        LoadError != null ? LoadError :
        (Staged.Count == 0 && Unstaged.Count == 0 && IsLoading) ? LoadingPlaceholder :
        null;

    public bool CommitEnabled =>
        !CommitBusy && !string.IsNullOrWhiteSpace(Title) && (Amend || Staged.Count > 0);
}
