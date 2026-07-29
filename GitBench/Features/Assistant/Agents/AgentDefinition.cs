using GitBench.Features.Assistant.Backend;

namespace GitBench.Features.Assistant.Agents;

/// One assistant persona: its system prompt, the tools it may call, and the model tier it runs on.
internal sealed record AgentDefinition(
    string Name,
    string SystemPrompt,
    IReadOnlyList<string> AllowedTools,
    ModelTier Tier);
