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
    public int? ReviewWindowX { get; init; }
    public int? ReviewWindowY { get; init; }
    public float RepoBarWidth { get; init; } = 220f;
    public bool RepoBarCollapsed { get; init; }
    public float BranchesWidth { get; init; } = 220f;
    public float CommitDetailsWidth { get; init; } = 380f;
    public float CommitDetailsSplitFraction { get; init; } = 2f / 3f;
    public FileViewMode FileViewMode { get; init; } = FileViewMode.Flat;
    public WorkingChangesLayout WorkingChangesLayout { get; init; } = WorkingChangesLayout.Diff;
    public bool HideRemoteOnlyBranches { get; init; }
    public bool EnableUntrackedCache { get; init; }

    /// <summary>Which model provider the assistant talks to. Null until one is chosen, which reads
    /// as the default provider.</summary>
    public string? AssistantProviderId { get; init; }

    /// <summary>What each provider was last given, so selecting one restores the model and endpoint
    /// used with it rather than starting from its defaults every time.</summary>
    public IReadOnlyList<AssistantProviderPreference> AssistantProviderPreferences { get; init; } = [];

    /// <summary>The assistant panel's size, remembered the way a window's is — one placement for the
    /// app, not one per session or per repository.</summary>
    public float AssistantPanelWidth { get; init; } = 380f;
    public float AssistantPanelHeight { get; init; } = 460f;

    /// <summary>Where the panel was left, measured from the host's top leading corner. Null until it
    /// has been moved once, which reads as the resting spot in the top trailing corner.</summary>
    public float? AssistantPanelX { get; init; }
    public float? AssistantPanelY { get; init; }

    public static Preferences Default { get; } = new();
}

/// <summary>The model and endpoint remembered for one assistant provider. Null on either means the
/// provider's own default. Held as plain strings so the preferences layer stays free of assistant
/// types.</summary>
public sealed record AssistantProviderPreference(string ProviderId, string? Model, string? BaseUrl);
