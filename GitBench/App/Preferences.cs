using GitBench.Features.LocalChanges;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Gui.Desktop;

namespace GitBench.App;

public sealed record Preferences
{
    public ThemeMode Theme { get; init; } = ThemeMode.Dark;
    public Locale Language { get; init; } = Locale.En;

    /// <summary>How much the UI is magnified on top of the monitor's own content scale. The monitor's
    /// scale is never stored: it is a property of the display, not of the user.</summary>
    public UiScale UiScale { get; init; } = UiScale.Default;

    // A zero size is what a minimized or not-yet-shown window reports; it never replaces a real one.
    public int WindowWidth { get => _windowWidth; init => _windowWidth = value > 0 ? value : _windowWidth; }
    public int WindowHeight { get => _windowHeight; init => _windowHeight = value > 0 ? value : _windowHeight; }
    private int _windowWidth = 1400;
    private int _windowHeight = 900;

    // Null until the window has been placed once; then the last on-screen top-left, restored
    // (clamped back on-screen) on next launch. May be negative on a multi-monitor layout.
    public int? WindowX { get; init; }
    public int? WindowY { get; init; }
    public int ReviewWindowWidth { get => _reviewWindowWidth; init => _reviewWindowWidth = value > 0 ? value : _reviewWindowWidth; }
    public int ReviewWindowHeight { get => _reviewWindowHeight; init => _reviewWindowHeight = value > 0 ? value : _reviewWindowHeight; }
    private int _reviewWindowWidth = 1100;
    private int _reviewWindowHeight = 800;
    public int? ReviewWindowX { get; init; }
    public int? ReviewWindowY { get; init; }
    public float RepoBarWidth { get; init; } = 220f;
    public bool RepoBarCollapsed { get; init; }
    public float BranchesWidth { get; init; } = 220f;
    public float CommitDetailsWidth { get; init; } = 380f;
    public float CommitDetailsSplitFraction { get; init; } = 2f / 3f;

    /// <summary>The file browser's tree rail. One width for the app, like every other splitter —
    /// the tree it sizes is per repo, but how wide someone wants a file tree is not.</summary>
    public float FileBrowserWidth { get; init; } = 260f;
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
    public float AssistantPanelWidth { get => _assistantPanelWidth; init => _assistantPanelWidth = value > 0f ? value : _assistantPanelWidth; }
    public float AssistantPanelHeight { get => _assistantPanelHeight; init => _assistantPanelHeight = value > 0f ? value : _assistantPanelHeight; }
    private float _assistantPanelWidth = 380f;
    private float _assistantPanelHeight = 460f;

    /// <summary>Where the panel was left, measured from the host's top leading corner. Null until it
    /// has been moved once, which reads as the resting spot in the top trailing corner.</summary>
    public float? AssistantPanelX { get; init; }
    public float? AssistantPanelY { get; init; }

    /// <summary>The shortcuts the user has changed from the built-in table; every other command
    /// runs on its default.</summary>
    public IReadOnlyList<KeyBinding> KeyBindings { get; init; } = [];

    /// <summary>Whether local agents may connect over MCP, and on which port.</summary>
    public bool AgentConnectionsEnabled { get; init; }
    public int AgentConnectionsPort { get; init; } = 5577;

    /// <summary>The secret path segment the endpoint carries. Null until the first enable
    /// generates one; kept afterwards so a command copied from the settings stays valid.</summary>
    public McpPathToken? AgentConnectionsToken { get; init; }

    /// <summary>The agent-connections preference as one move: on or off, the port, and the
    /// endpoint's token. A port outside 1–65535 leaves everything as it was, since no server could
    /// bind it.</summary>
    public Preferences WithAgentConnections(bool enabled, int port, McpPathToken? token) =>
        port is < 1 or > 65535
            ? this
            : this with
            {
                AgentConnectionsEnabled = enabled,
                AgentConnectionsPort = port,
                AgentConnectionsToken = token,
            };

    public static Preferences Default { get; } = new();
}

/// <summary>The model and endpoint remembered for one assistant provider. Null on either means the
/// provider's own default. Held as plain strings so the preferences layer stays free of assistant
/// types.</summary>
public sealed record AssistantProviderPreference(string ProviderId, string? Model, string? BaseUrl);
