using GitBench.Features.Assistant.Backend;

namespace GitBench.Features.Assistant.Agents;

/// One assistant persona: its system prompt, the tools it may call, and the role whose model it
/// runs on.
internal sealed record AgentDefinition(
    string Name,
    string SystemPrompt,
    IReadOnlyList<string> AllowedTools,
    AssistantRole Role);
