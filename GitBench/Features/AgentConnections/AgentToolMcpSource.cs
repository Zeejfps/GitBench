using GitBench.Features.Assistant.Tools;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Review.Walkthrough;
using GitBench.Git;
using McpSdk.Adapter.System.Text.Json;
using McpSdk.Protocol;
using McpSdk.Protocol.Models;
using McpSdk.Server;
using ZGF.Gui.Desktop;
using ZGF.Observable;

namespace GitBench.Features.AgentConnections;

/// <summary>
/// Serves the assistant's review tools to MCP clients. Each client session gets its own handlers,
/// bound to an <see cref="AgentSession"/> that remembers which repository it drives and which
/// walkthroughs it narrates; a call resolves its repository on the UI thread, builds the tool set
/// for it, and runs the assistant tool as the assistant would.
/// </summary>
internal sealed class AgentToolMcpSource : IMcpToolSource
{
    private readonly AgentToolExport _export;
    private readonly AgentRepoResolver _resolver;
    private readonly IReviewWindowRegistry _windows;
    private readonly AssistantWriteSurface _surface;
    private readonly TimeProvider _clock;
    private readonly SystemJson _json = new();
    private readonly State<int> _openSessions = new(0);

    public AgentToolMcpSource(
        AgentToolExport export,
        IRepoRegistry registry,
        IReviewWindowRegistry windows,
        AssistantWriteSurface surface,
        TimeProvider clock)
    {
        _export = export;
        _resolver = new AgentRepoResolver(registry, windows);
        _windows = windows;
        _surface = surface;
        _clock = clock;
    }

    /// <summary>How many client sessions are open. UI-thread state.</summary>
    public IReadable<int> OpenSessions => _openSessions;

    public void Register(McpSession session, DefaultToolsController tools)
    {
        var agent = new AgentSession(session, _surface.Dispatcher, _clock);
        foreach (var definition in _export.Definitions())
            tools.AddTool(new Handler(this, agent, definition));

        _surface.Dispatcher.Post(() => _openSessions.Value++);
        session.Ended.Register(() => _surface.Dispatcher.Post(() =>
        {
            agent.End();
            _openSessions.Value--;
        }));
    }

    // UI thread: the repository a call means, and the tool bound to it.
    private Bound Bind(AgentSession agent, string name, AgentToolRole role, string? repoArgument)
    {
        switch (_resolver.Resolve(repoArgument, agent.Default))
        {
            case RepoResolution.Resolved resolved:
                return BindTo(agent, name, role, resolved.Repo);
            case RepoResolution.Unknown unknown:
                return new Bound.Missing(
                    $"No open repository matches '{unknown.Given}'. Open repositories: {Describe(unknown.Open)}");
            case RepoResolution.NothingOpen:
                return new Bound.Missing("No repository is open in DiffDino. Open one there, then call again.");
            default:
                throw new InvalidOperationException("Unhandled repository resolution.");
        }
    }

    private Bound BindTo(AgentSession agent, string name, AgentToolRole role, Repo repo)
    {
        IAssistantTool? tool = null;
        foreach (var exported in _export.ForRepo(repo))
            if (exported.Tool.Name == name)
                tool = exported.Tool;
        if (tool is null)
            throw new InvalidOperationException($"The exported tool '{name}' is missing from the set built for '{repo.DisplayName}'.");

        switch (role)
        {
            case AgentToolRole.Repository:
                return new Bound.Ready(tool);
            case AgentToolRole.Presentation:
                agent.NoteDriving(repo);
                return new Bound.Ready(tool);
            case AgentToolRole.Walkthrough:
                agent.NoteDriving(repo);
                if (Walkthrough(repo) is not { } store) return new Bound.Ready(tool);
                agent.NoteNarrating(store);
                return new Bound.ReadyNarrating(tool, store);
            case AgentToolRole.Pairing:
                agent.NoteDriving(repo);
                return new Bound.Ready(tool);
            default:
                throw new InvalidOperationException($"Unhandled tool role {role}.");
        }
    }

    // The store a walkthrough call lands on: the most recently opened window for the repository,
    // which is the one the tool itself picks.
    private ReviewWalkthroughStore? Walkthrough(Repo repo)
    {
        ReviewWalkthroughStore? found = null;
        foreach (var window in _windows.Windows)
            if (window.Session.RepoId == repo.Id)
                found = window.Walkthrough;
        return found;
    }

    private static string Describe(IReadOnlyList<Repo> open) =>
        open.Count == 0 ? "(none)" : string.Join("; ", open.Select(r => $"{r.DisplayName} ({r.Path})"));

    private abstract record Bound
    {
        public sealed record Ready(IAssistantTool Tool) : Bound;

        /// <summary>A walkthrough tool with a window to narrate in.</summary>
        public sealed record ReadyNarrating(IAssistantTool Tool, ReviewWalkthroughStore Store) : Bound;

        public sealed record Missing(string Message) : Bound;
    }

    private sealed class Handler : IToolHandler
    {
        private readonly AgentToolMcpSource _owner;
        private readonly AgentSession _agent;
        private readonly AgentToolRole _role;

        public Handler(AgentToolMcpSource owner, AgentSession agent, ExportedTool definition)
        {
            _owner = owner;
            _agent = agent;
            _role = definition.Role;
            Tool = AgentToolSchema.Describe(definition.Tool);
        }

        public Tool Tool { get; }

        public async Task<CallToolResult> Call(IJsonObject arguments, McpRequestContext context)
        {
            string? repoArgument;
            System.Text.Json.JsonElement rest;
            switch (AgentToolSchema.SplitArguments(_owner._json, arguments))
            {
                case AgentToolSchema.Arguments.Split split:
                    repoArgument = split.Repo;
                    rest = split.Rest;
                    break;
                case AgentToolSchema.Arguments.Invalid invalid:
                    return CallToolResult.Error(new TextContent(invalid.Message));
                default:
                    throw new InvalidOperationException("Unhandled argument split.");
            }

            // The session's end releases a tool blocked on the reviewer the same way the client's
            // own cancellation does.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _agent.Ended);
            var ct = linked.Token;

            var bound = await _owner._surface
                .OnUiThreadAsync(() => _owner.Bind(_agent, Tool.Name, _role, repoArgument), ct)
                .ConfigureAwait(false);

            ToolInvocation result;
            switch (bound)
            {
                case Bound.Ready ready:
                    result = await ready.Tool.InvokeAsync(rest, ct).ConfigureAwait(false);
                    break;
                case Bound.ReadyNarrating narrating:
                    result = await narrating.Tool.InvokeAsync(rest, ct).ConfigureAwait(false);
                    _owner._surface.Dispatcher.Post(() => _agent.WatchPresence(narrating.Store));
                    break;
                case Bound.Missing missing:
                    return CallToolResult.Error(new TextContent(missing.Message));
                default:
                    throw new InvalidOperationException("Unhandled binding.");
            }

            return result.IsError
                ? CallToolResult.Error(new TextContent(result.Content))
                : CallToolResult.Ok(new TextContent(result.Content));
        }
    }
}
