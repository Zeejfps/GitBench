using GitBench.Features.Assistant.Tools;
using GitBench.Features.CodeIntel;
using GitBench.Features.Pairing;
using GitBench.Features.Review;
using GitBench.Git;

namespace GitBench.Features.AgentConnections;

/// <summary>What an exported tool touches, which decides what the bridge notes about the call:
/// a repository read (or a Viewed mark), the review window's presentation, or the walkthrough
/// a session narrates.</summary>
internal enum AgentToolRole
{
    Repository,
    Presentation,
    Walkthrough,
    Pairing,
}

/// <summary>One assistant tool as the MCP bridge serves it, with the role it plays.</summary>
internal sealed record ExportedTool(IAssistantTool Tool, AgentToolRole Role);

/// <summary>
/// The assistant tools a terminal agent reaches over MCP: the review reads, the Viewed mark, the
/// presentation tools, the blocking walkthrough and the pairing loop. Never the repository-mutating
/// writes — the agent has git of its own.
/// </summary>
internal sealed class AgentToolExport
{
    private readonly IGitService _git;
    private readonly ISymbolExtractor _extractor;
    private readonly IReviewProgressStore _progress;
    private readonly IReviewWindowRegistry _windows;
    private readonly AssistantWriteSurface _surface;
    private readonly IPairingSessions _pairing;

    public AgentToolExport(
        IGitService git,
        ISymbolExtractor extractor,
        IReviewProgressStore progress,
        IReviewWindowRegistry windows,
        AssistantWriteSurface surface,
        IPairingSessions pairing)
    {
        _git = git;
        _extractor = extractor;
        _progress = progress;
        _windows = windows;
        _surface = surface;
        _pairing = pairing;
    }

    /// <summary>The tools bound to one repository. Cheap: each holds the services and the repo.</summary>
    public IReadOnlyList<ExportedTool> ForRepo(Repo repo) =>
    [
        .. ReviewTools.CreateReads(_git, repo, _extractor).Select(t => new ExportedTool(t, AgentToolRole.Repository)),
        .. ReviewTools.CreateWrites(_git, repo, _progress, _surface).Select(t => new ExportedTool(t, AgentToolRole.Repository)),
        .. ReviewPresentationTools.CreateAll(_git, repo, _windows, _surface).Select(t => new ExportedTool(t, AgentToolRole.Presentation)),
        .. WalkthroughTools.CreateAll(repo, _windows, _surface.Dispatcher, WalkthroughNarratorMode.Blocking).Select(t => new ExportedTool(t, AgentToolRole.Walkthrough)),
        .. PairingTools.CreateAll(repo, _pairing, _surface.Dispatcher).Select(t => new ExportedTool(t, AgentToolRole.Pairing)),
    ];

    /// <summary>
    /// The tools' names, descriptions and schemas, which are per tool type rather than per
    /// repository. Read off a set bound to a placeholder repository that is never invoked — a
    /// client lists tools before any repository is named, and may do so with none open.
    /// </summary>
    public IReadOnlyList<ExportedTool> Definitions() => ForRepo(Placeholder);

    private static readonly Repo Placeholder = new(Guid.Empty, string.Empty, "placeholder");
}
