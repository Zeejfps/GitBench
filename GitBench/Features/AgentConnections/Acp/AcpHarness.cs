using System.Text.Json.Nodes;

namespace GitBench.Features.AgentConnections.Acp;

/// <summary>Identifies an ACP harness preset. Persisted, so the values never change meaning.</summary>
internal readonly record struct AcpHarnessId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// An agent the app can run over the Agent Client Protocol: the adapter command to start, and the
/// session mode in which that adapter hands writes to the client as permission requests instead of
/// deciding them itself. A harness with no such mode can't be guarded, and is refused. Some adapters
/// also take tools away outright through <see cref="SessionMetaJson"/>, sent as <c>_meta</c> on
/// <c>session/new</c>: a CLI's own allow rules decide without asking the client, and a tool that
/// isn't there can't be allowed. Between pairing sessions a chat may run in a looser
/// <see cref="ChatMode"/>; a pairing session always runs in the asking mode.
/// </summary>
internal sealed record AcpHarness(
    AcpHarnessId Id, string Label, string Command, IReadOnlyList<string> Args, string AskingMode, string? SessionMetaJson = null,
    string? ChatMode = null)
{
    public static readonly AcpHarness ClaudeCode = new(
        new AcpHarnessId("claude"), "Claude Code", "npx", ["-y", "@agentclientprotocol/claude-agent-acp"], "default");

    public static readonly AcpHarness Codex = new(
        new AcpHarnessId("codex"), "Codex", "npx", ["-y", "@agentclientprotocol/codex-acp"], "read-only");

    public static readonly AcpHarness Gemini = new(
        new AcpHarnessId("gemini"), "Gemini CLI", "gemini", ["--experimental-acp"], "default");

    /// <summary>The mode a turn runs in: the asking mode while a pairing session is live, else the
    /// chat's own.</summary>
    public string ModeFor(bool pairing) => pairing ? AskingMode : ChatMode ?? AskingMode;

    /// <summary>The harness that runs <paramref name="preset"/>. Claude Code's adapter takes no CLI
    /// arguments of its own, so its flags travel in the session's <c>_meta</c>, which the adapter
    /// hands to the CLI; the other adapters take them on their command line.</summary>
    public static AcpLaunch For(AgentPreset preset)
    {
        var agent = Of(preset.Kind);
        var chatMode = ModeOf(preset.Kind, preset.Permission);
        var id = new AcpHarnessId(preset.Id.Value);
        switch (preset.Kind)
        {
            case AgentKind.ClaudeCode:
                if (AgentArguments.ClaudeFlags(preset.Arguments, out var flags) is { } problem)
                    return new AcpLaunch.Invalid(problem);
                return new AcpLaunch.Ready(agent with
                {
                    Id = id, Label = preset.Name, ChatMode = chatMode, SessionMetaJson = ClaudeMeta(flags),
                });
            case AgentKind.Codex:
            case AgentKind.Gemini:
                return new AcpLaunch.Ready(agent with
                {
                    Id = id, Label = preset.Name, ChatMode = chatMode, Args = [.. agent.Args, .. preset.Arguments],
                });
            default:
                throw new ArgumentOutOfRangeException(nameof(preset), preset.Kind, "Unknown agent.");
        }
    }

    private static AcpHarness Of(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => ClaudeCode,
        AgentKind.Codex => Codex,
        AgentKind.Gemini => Gemini,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown agent."),
    };

    private static string ModeOf(AgentKind kind, AgentPermission permission) => (kind, permission) switch
    {
        (AgentKind.ClaudeCode, AgentPermission.Ask) => "default",
        (AgentKind.ClaudeCode, AgentPermission.AcceptEdits) => "acceptEdits",
        (AgentKind.ClaudeCode, AgentPermission.Bypass) => "bypassPermissions",
        (AgentKind.Codex, AgentPermission.Ask) => "read-only",
        (AgentKind.Codex, AgentPermission.AcceptEdits) => "auto",
        (AgentKind.Codex, AgentPermission.Bypass) => "full-access",
        (AgentKind.Gemini, AgentPermission.Ask) => "default",
        (AgentKind.Gemini, AgentPermission.AcceptEdits) => "autoEdit",
        (AgentKind.Gemini, AgentPermission.Bypass) => "yolo",
        _ => throw new ArgumentOutOfRangeException(nameof(permission), (kind, permission), "Unknown permission."),
    };

    // The SDK takes model and effort as options of their own and passes each other flag to the CLI
    // as --name [value]; a null value is a flag with none.
    private static string? ClaudeMeta(IReadOnlyList<ClaudeFlag> flags)
    {
        if (flags.Count == 0) return null;
        var options = new JsonObject();
        var extra = new JsonObject();
        foreach (var flag in flags)
        {
            if (flag is { Name: "model" or "effort", Value: { } value }) options[flag.Name] = value;
            else extra[flag.Name] = flag.Value;
        }

        if (extra.Count > 0) options["extraArgs"] = extra;
        return new JsonObject { ["claudeCode"] = new JsonObject { ["options"] = options } }.ToJsonString();
    }
}

/// <summary>Whether a preset can be run, and the harness that runs it.</summary>
internal abstract record AcpLaunch
{
    public sealed record Ready(AcpHarness Harness) : AcpLaunch;

    public sealed record Invalid(AgentArgumentsProblem Problem) : AcpLaunch;
}
