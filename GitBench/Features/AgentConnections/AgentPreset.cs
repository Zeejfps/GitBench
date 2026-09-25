using GitBench.Localization;

namespace GitBench.Features.AgentConnections;

/// <summary>Identifies an agent preset. The built-in presets keep the ids the agents had before
/// presets existed, so a remembered pick still finds its agent.</summary>
public readonly record struct AgentPresetId(string Value)
{
    public static AgentPresetId New() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;
}

/// <summary>The agent CLI a preset runs.</summary>
public enum AgentKind
{
    ClaudeCode,
    Codex,
    Gemini,
}

/// <summary>How much a preset's agent may do in a chat without asking. A pairing session always
/// asks, whatever the preset says.</summary>
public enum AgentPermission
{
    Ask,
    AcceptEdits,
    Bypass,
}

/// <summary>A named way to run an agent: which CLI, what it may do without asking, and the extra
/// arguments it is given.</summary>
public sealed record AgentPreset(
    AgentPresetId Id, string Name, AgentKind Kind, AgentPermission Permission, IReadOnlyList<string> Arguments)
{
    public static readonly AgentPreset ClaudeCode = new(new AgentPresetId("claude"), "Claude Code", AgentKind.ClaudeCode, AgentPermission.Ask, []);
    public static readonly AgentPreset Codex = new(new AgentPresetId("codex"), "Codex", AgentKind.Codex, AgentPermission.Ask, []);
    public static readonly AgentPreset Gemini = new(new AgentPresetId("gemini"), "Gemini CLI", AgentKind.Gemini, AgentPermission.Ask, []);

    public static readonly IReadOnlyList<AgentPreset> BuiltIn = [ClaudeCode, Codex, Gemini];

    public bool Equals(AgentPreset? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && Kind == other.Kind
        && Permission == other.Permission
        && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Kind, Permission, Arguments.Count);
}

internal static class AgentKinds
{
    public static readonly IReadOnlyList<AgentKind> All = [AgentKind.ClaudeCode, AgentKind.Codex, AgentKind.Gemini];

    /// <summary>The CLI's product name, which is not translated.</summary>
    public static string Label(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "Claude Code",
        AgentKind.Codex => "Codex",
        AgentKind.Gemini => "Gemini CLI",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown agent."),
    };

    /// <summary>What running a <paramref name="kind"/> agent takes, in a line.</summary>
    public static string Detail(AgentKind kind, Strings s) => kind switch
    {
        AgentKind.ClaudeCode => s.PairingAgentClaudeDetail,
        AgentKind.Codex => s.PairingAgentCodexDetail,
        AgentKind.Gemini => s.PairingAgentGeminiDetail,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown agent."),
    };
}
