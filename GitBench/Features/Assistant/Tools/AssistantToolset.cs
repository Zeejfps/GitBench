using GitBench.Features.Assistant.Agents;
using GitBench.Features.Review;
using GitBench.Git;

namespace GitBench.Features.Assistant.Tools;

/// <summary>
/// The tools one agent may invoke against one repository.
/// </summary>
/// <remarks>
/// Built per repo, so the assistant cannot reach any other checkout, and filtered by the agent's
/// allowed list. Order is ordinal by name: the serialized tool list heads the prompt-cache prefix,
/// so it has to be byte-identical from turn to turn.
/// </remarks>
internal sealed class AssistantToolset
{
    private readonly Dictionary<string, IAssistantTool> _byName;

    private AssistantToolset(IReadOnlyList<IAssistantTool> tools)
    {
        Tools = tools;
        _byName = new Dictionary<string, IAssistantTool>(tools.Count, StringComparer.Ordinal);
        foreach (var tool in tools)
            _byName[tool.Name] = tool;
    }

    public IReadOnlyList<IAssistantTool> Tools { get; }

    public IAssistantTool? Find(string name) => _byName.GetValueOrDefault(name);

    public static AssistantToolset Create(IEnumerable<IAssistantTool> tools, IReadOnlyCollection<string> allowed)
    {
        var permitted = new HashSet<string>(allowed, StringComparer.Ordinal);
        var filtered = tools
            .Where(tool => permitted.Contains(tool.Name))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();
        return new AssistantToolset(filtered);
    }

    /// <summary>The reads alone, for a caller with nothing for a write to act on.</summary>
    public static AssistantToolset ForRepo(IGitService git, Repo repo, AgentDefinition agent) =>
        Create(Reads(git, repo), agent.AllowedTools);

    public static AssistantToolset ForRepo(
        IGitService git,
        Repo repo,
        AgentDefinition agent,
        IReviewProgressStore reviewProgress,
        AssistantWriteSurface writes) =>
        Create(
            [
                .. Reads(git, repo),
                .. WriteTools.CreateAll(git, repo, writes),
                .. ReviewTools.CreateWrites(git, repo, reviewProgress, writes),
            ],
            agent.AllowedTools);

    private static IEnumerable<IAssistantTool> Reads(IGitService git, Repo repo) =>
        [.. ReadTools.CreateAll(git, repo), .. ReviewTools.CreateReads(git, repo), new ReadFileTool(git, repo)];
}
