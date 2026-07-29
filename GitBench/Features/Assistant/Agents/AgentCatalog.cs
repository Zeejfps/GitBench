using System.Reflection;
using GitBench.Features.Assistant.Backend;

namespace GitBench.Features.Assistant.Agents;

/// <summary>
/// The agents the app ships with.
/// </summary>
/// <remarks>
/// Each agent is one embedded markdown file: a <c>---</c> fenced header carrying <c>name</c>,
/// <c>tier</c> and a comma-separated <c>tools</c> list, then the system prompt as the body. Adding
/// an agent is adding a file — the catalog enumerates every embedded <c>.md</c> resource.
/// </remarks>
internal sealed class AgentCatalog
{
    public const string GeneralAgent = "general";

    public const string CommitMessageAgent = "commit-message";

    /// The one-shot agents behind the diff selection's quick actions.
    public const string ExplainSelectionAgent = "explain-selection";

    public const string BreakageSelectionAgent = "breakage-selection";

    public const string FixSelectionAgent = "fix-selection";

    private const string Fence = "---";

    private readonly Dictionary<string, AgentDefinition> _agents;

    private AgentCatalog(Dictionary<string, AgentDefinition> agents) => _agents = agents;

    public AgentDefinition Get(string name) =>
        _agents.TryGetValue(name, out var agent)
            ? agent
            : throw new InvalidOperationException($"No assistant agent named '{name}' is embedded.");

    public static AgentCatalog LoadEmbedded() => LoadFrom(typeof(AgentCatalog).Assembly);

    public static AgentCatalog LoadFrom(Assembly assembly)
    {
        var agents = new Dictionary<string, AgentDefinition>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
                continue;

            using var reader = new StreamReader(stream);
            var agent = Parse(resource, reader.ReadToEnd());
            agents[agent.Name] = agent;
        }

        if (agents.Count == 0)
            throw new InvalidOperationException("No assistant agent prompts are embedded in the assembly.");

        return new AgentCatalog(agents);
    }

    internal static AgentDefinition Parse(string resource, string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith(Fence + "\n", StringComparison.Ordinal))
            throw new InvalidOperationException($"Agent prompt '{resource}' has no header block.");

        var end = normalized.IndexOf("\n" + Fence, Fence.Length, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException($"Agent prompt '{resource}' has an unterminated header block.");

        var header = normalized[(Fence.Length + 1)..end];
        var bodyStart = normalized.IndexOf('\n', end + 1);
        var body = bodyStart < 0 ? string.Empty : normalized[(bodyStart + 1)..].Trim();

        string? name = null;
        var tier = ModelTier.Chat;
        var tools = Array.Empty<string>();
        foreach (var line in header.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator < 0)
                continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "name":
                    name = value;
                    break;
                case "tier":
                    tier = value.Equals("quick", StringComparison.OrdinalIgnoreCase) ? ModelTier.Quick : ModelTier.Chat;
                    break;
                case "tools":
                    tools = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"Agent prompt '{resource}' has no name.");
        if (body.Length == 0)
            throw new InvalidOperationException($"Agent prompt '{resource}' has an empty system prompt.");

        return new AgentDefinition(name, body, tools, tier);
    }
}
