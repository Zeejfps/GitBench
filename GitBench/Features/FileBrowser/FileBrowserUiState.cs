namespace GitBench.Features.FileBrowser;

/// <summary>
/// Persisted per-repo state for the file browser: which directories are open, whether ignored and
/// hidden entries are listed, which files are open in the strip, and where the cursor was left.
/// Paths are repo-relative and slash-separated so moving a checkout does not orphan every one of
/// them.
/// </summary>
/// <remarks>
/// A shell cannot survive a restart and the terminal's session-only store is shaped around that; a
/// set of open directories can, and the branches sidebar has already set the expectation that a
/// tree comes back the way it was left. So this rides <see cref="Repos.IRepoRegistry"/>'s state file
/// alongside <see cref="Branches.BranchesUiState"/>.
/// </remarks>
public sealed class FileBrowserUiState
{
    public List<string> Expanded { get; set; } = [];
    public bool ShowHidden { get; set; } = true;
    public string? Cursor { get; set; }
    public bool RenderMarkdown { get; set; } = true;

    /// <summary>The open tabs, in strip order. Only files inside the working tree — a tab on
    /// anything else is a place this repository cannot name.</summary>
    public List<string> Tabs { get; set; } = [];

    /// <summary>Which of <see cref="Tabs"/> was on screen. Falls back to <see cref="Cursor"/>'s
    /// file when it names nothing.</summary>
    public string? ActiveTab { get; set; }

    public FileBrowserUiState Clone() => new()
    {
        Expanded = [.. Expanded],
        ShowHidden = ShowHidden,
        Cursor = Cursor,
        RenderMarkdown = RenderMarkdown,
        Tabs = [.. Tabs],
        ActiveTab = ActiveTab,
    };
}
