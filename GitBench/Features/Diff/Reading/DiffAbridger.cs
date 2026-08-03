using System.Text.Json;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using GitBench.Git;
using GitBench.Localization;

namespace GitBench.Features.Diff.Reading;

/// <summary>Why an abridgement produced nothing, in the words the reader is shown.</summary>
internal sealed record ReadingFailure(string Message);

/// <summary>
/// Turns a set of diffs into a reading plan: cache first, then the agent.
/// </summary>
/// <remarks>
/// A cache hit costs nothing and needs no credentials, which is what makes reading mode worth
/// leaving on. A miss is a real agent run — a minute or so, and a large share of a model context —
/// so it happens once per change and every later look at the same diff is free.
/// </remarks>
internal sealed class DiffAbridger
{
    public const string AgentName = "reading-diff";

    private const string Instruction =
        "Abridge the diff below into a reading diff. Plan against the numbered rows and finish with "
        + "submit_plan.\n\n";

    private readonly IGitService _git;
    private readonly Repo _repo;
    private readonly AgentCatalog _catalog;
    private readonly IAssistantBackend _backend;
    private readonly ILocalizationService _loc;
    private readonly ReadingPlanStore _store;
    private readonly Func<string> _model;

    public DiffAbridger(
        IGitService git,
        Repo repo,
        AgentCatalog catalog,
        IAssistantBackend backend,
        ILocalizationService loc,
        Func<string> model,
        ReadingPlanStore? store = null)
    {
        _git = git;
        _repo = repo;
        _catalog = catalog;
        _backend = backend;
        _loc = loc;
        _model = model;
        _store = store ?? new ReadingPlanStore();
    }

    /// <summary>Short status lines while a run is in flight, for the header to show. Called off the
    /// UI thread.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>The plan for these diffs, from the cache when it is there and from the agent
    /// otherwise. Null with a reason when no plan could be produced.</summary>
    public async Task<(ReadingOverlay? Overlay, ReadingFailure? Failure)> AbridgeAsync(
        IReadOnlyList<DiffResult> files,
        CancellationToken ct)
    {
        var readable = files.Where(f => f is { IsBinary: false, ErrorMessage: null, Hunks.Count: > 0 }).ToArray();
        if (readable.Length == 0)
            return (null, new ReadingFailure(_loc.Strings.Value.ReadingNothingToAbridge));

        var index = ReadingRowIndex.Build(readable);
        var agent = _catalog.Get(AgentName);
        var key = ReadingPlanStore.Key(index, _model(), ReadingSurface.Hash(agent.SystemPrompt));

        if (_store.Load(key, index) is { } cached)
            return (cached, null);

        var abridgement = new ReadingAbridgement(index);
        var toolset = AssistantToolset.Create(
            [.. ReadTools.CreateAll(_git, _repo), new ReadFileTool(_git, _repo), new FindFilesTool(_git, _repo), .. abridgement.Tools],
            agent.AllowedTools);

        var loop = new AssistantAgentLoop(_backend, agent, toolset);
        var run = new OneShotAgentRun(loop, ReadOnlyGate.Instance, SubmitPlanTool.ToolName)
        {
            OnProgress = OnProgress,
        };

        var outcome = await run
            .RunAsync(
                Instruction + index.Render(),
                AssistantAgentLoop.RepoStateBlock(_git, _repo, _loc),
                ct)
            .ConfigureAwait(false);

        if (outcome.Cancelled) return (null, null);
        if (abridgement is { Result: { } overlay, Plan: { } plan })
        {
            _store.Save(key, plan);
            return (overlay, null);
        }

        var reason = outcome.Failure is { Length: > 0 } failure
            ? failure
            : _loc.Strings.Value.ReadingNoPlanProduced;
        return (null, new ReadingFailure(reason));
    }

}

/// <summary>
/// The gate for a run that only decides what is drawn: nothing in the repository can change, so
/// there is nothing to ask about, and a write tool reaching this point is a bug rather than a
/// request.
/// </summary>
internal sealed class ReadOnlyGate : IToolApprovalGate
{
    public static readonly ReadOnlyGate Instance = new();

    private ReadOnlyGate() { }

    public Task<bool> RequestAsync(string toolName, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(false);
}
