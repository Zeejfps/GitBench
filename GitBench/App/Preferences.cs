using GitBench.Features.LocalChanges;
using GitBench.Localization;
using GitBench.Theming;

namespace GitBench.App;

public sealed record Preferences
{
    public ThemeMode Theme { get; init; } = ThemeMode.Dark;
    public Locale Language { get; init; } = Locale.En;
    public int WindowWidth { get; init; } = 1400;
    public int WindowHeight { get; init; } = 900;

    // Null until the window has been placed once; then the last on-screen top-left, restored
    // (clamped back on-screen) on next launch. May be negative on a multi-monitor layout.
    public int? WindowX { get; init; }
    public int? WindowY { get; init; }
    public int ReviewWindowWidth { get; init; } = 1100;
    public int ReviewWindowHeight { get; init; } = 800;
    public float RepoBarWidth { get; init; } = 220f;
    public float BranchesWidth { get; init; } = 220f;
    public float CommitDetailsWidth { get; init; } = 380f;
    public float CommitDetailsSplitFraction { get; init; } = 2f / 3f;
    public FileViewMode FileViewMode { get; init; } = FileViewMode.Flat;
    public WorkingChangesLayout WorkingChangesLayout { get; init; } = WorkingChangesLayout.List;
    public bool HideRemoteOnlyBranches { get; init; }

    public static Preferences Default { get; } = new();
}
