using GitBench.Localization;

namespace GitBench.Features.AgentConnections;

/// <summary>The translated text the app shows for an <see cref="AgentKind"/>.</summary>
internal static class AgentKindText
{
    /// <summary>What running a <paramref name="kind"/> agent takes, in a line.</summary>
    public static string Detail(AgentKind kind, Strings s) => kind switch
    {
        AgentKind.ClaudeCode => s.PairingAgentClaudeDetail,
        AgentKind.Codex => s.PairingAgentCodexDetail,
        AgentKind.Gemini => s.PairingAgentGeminiDetail,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown agent."),
    };
}
