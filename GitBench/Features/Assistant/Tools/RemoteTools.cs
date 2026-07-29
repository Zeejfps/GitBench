using System.Text.Json;
using GitBench.Features.Repos;
using GitBench.Git;

namespace GitBench.Features.Assistant.Tools;

/// <summary>
/// The two tools that reach the repository's remotes: fetching what is there, and pulling it into
/// the checked-out branch.
/// </summary>
/// <remarks>
/// Both go through <see cref="IRepoOperationsStore"/> rather than calling git themselves, so an
/// operation the model started is the same one the toolbar button starts — the same spinner while it
/// runs, the same toast when it lands, the same error badge on a repository the user is not looking
/// at, and the same reconcile dialog when a pull diverges. The store keeps its per-repo state on the
/// UI thread with no lock, so each tool hands the call to that thread and waits for what it reports
/// rather than calling in from the thread the turn runs on.
/// </remarks>
internal static class RemoteTools
{
    public static IReadOnlyList<IAssistantTool> CreateAll(Repo repo, AssistantWriteSurface surface) =>
    [
        new FetchTool(repo, surface),
        new PullTool(repo, surface),
    ];

    /// <summary>Starts the operation where the store lives, then waits for the outcome it reports.</summary>
    internal static async Task<RemoteOpResult> RunAsync(
        AssistantWriteSurface surface,
        Func<IRepoOperationsStore, Task<RemoteOpResult>> start,
        CancellationToken ct)
    {
        var running = await surface.OnUiThreadAsync(() => start(surface.Operations), ct).ConfigureAwait(false);
        return await running.WaitAsync(ct).ConfigureAwait(false);
    }
}

internal sealed class FetchTool : IAssistantTool
{
    private readonly Repo _repo;
    private readonly AssistantWriteSurface _surface;

    public FetchTool(Repo repo, AssistantWriteSurface surface)
    {
        _repo = repo;
        _surface = surface;
    }

    public string Name => "fetch";

    public string Description =>
        "Fetches from this repository's remotes, exactly as pressing Fetch does. Nothing on a local "
        + "branch or in the working tree moves — only the remote-tracking refs, so get_status and "
        + "get_branches read the remote's current position afterwards rather than a stale one.";

    public string JsonSchema =>
        """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsWrite => true;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var result = await RemoteTools.RunAsync(_surface, store => store.FetchAsync(_repo), ct).ConfigureAwait(false);
        return result switch
        {
            RemoteOpResult.Succeeded => ToolInvocation.Ok(ToolJson.Write(writer => writer.WriteBoolean("ok", true))),
            // Nothing was fetched, and this cannot come back as a success: an ok result here is one
            // step from telling the person their repository is up to date when it was never asked.
            RemoteOpResult.AlreadyRunning => ToolInvocation.Error(
                $"A fetch was already running on '{_repo.DisplayName}', so this call fetched nothing. "
                + "Wait for that one rather than asking again."),
            RemoteOpResult.Failed failed => ToolInvocation.Error(failed.Message),
            _ => ToolInvocation.Error("The fetch did not report an outcome."),
        };
    }
}

internal sealed class PullTool : IAssistantTool
{
    // The three ways git can reconcile a diverged branch, named as the model passes them.
    private static readonly (string Name, PullStrategy Value)[] Strategies =
    [
        ("merge", PullStrategy.Merge),
        ("rebase", PullStrategy.Rebase),
        ("ff_only", PullStrategy.FastForwardOnly),
    ];

    private readonly Repo _repo;
    private readonly AssistantWriteSurface _surface;

    public PullTool(Repo repo, AssistantWriteSurface surface)
    {
        _repo = repo;
        _surface = surface;
    }

    public string Name => "pull";

    public string Description =>
        "Pulls the checked-out branch's upstream into it, exactly as pressing Pull does. Omit "
        + "'strategy' to take whatever the repository is configured to do; pass one only when a "
        + "pull came back saying the branch has diverged and git refused to choose. A pull can "
        + "leave the working tree conflicted — get_conflicts says whether it did.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"strategy":{"type":"string","enum":["merge","rebase","ff_only"],"description":"How to reconcile a diverged branch. Omit for the repository's configured default."}},"additionalProperties":false}
        """;

    public bool IsWrite => true;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var named = ToolJson.String(args, "strategy")?.Trim();
        PullStrategy? strategy = null;
        if (named is { Length: > 0 })
        {
            if (!TryStrategy(named, out var picked))
                return ToolInvocation.Error(
                    $"'{named}' is not a pull strategy. Use 'merge', 'rebase' or 'ff_only', or omit "
                    + "'strategy' to take the repository's configured default.");
            strategy = picked;
        }

        var result = await RemoteTools.RunAsync(_surface, store => store.PullAsync(_repo, strategy), ct)
            .ConfigureAwait(false);
        return result switch
        {
            RemoteOpResult.Succeeded => ToolInvocation.Ok(ToolJson.Write(writer =>
            {
                writer.WriteBoolean("ok", true);
                if (named is { Length: > 0 }) writer.WriteString("strategy", named);
            })),
            // Git declined to pick, so nothing moved. The retry has to name a strategy, and saying
            // which ones exist is the difference between that and the same call again.
            RemoteOpResult.Diverged => ToolInvocation.Error(
                "The branch and its upstream have both moved on, so git will not choose how to "
                + "reconcile them and nothing was pulled. Call again with 'strategy' set to 'merge' "
                + "or 'rebase' — or 'ff_only' to confirm it should refuse rather than reconcile."),
            RemoteOpResult.AlreadyRunning => ToolInvocation.Error(
                $"A pull was already running on '{_repo.DisplayName}', so this call pulled nothing. "
                + "Wait for that one rather than asking again."),
            RemoteOpResult.Failed failed => ToolInvocation.Error(failed.Message),
            _ => ToolInvocation.Error("The pull did not report an outcome."),
        };
    }

    private static bool TryStrategy(string name, out PullStrategy strategy)
    {
        foreach (var (candidate, value) in Strategies)
        {
            if (!string.Equals(candidate, name, StringComparison.Ordinal)) continue;
            strategy = value;
            return true;
        }

        strategy = default;
        return false;
    }
}
