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
/// isn't there can't be allowed.
/// </summary>
internal sealed record AcpHarness(
    AcpHarnessId Id, string Label, string Command, IReadOnlyList<string> Args, string AskingMode, string? SessionMetaJson = null)
{
    public static readonly AcpHarness ClaudeCode = new(
        new AcpHarnessId("claude"), "Claude Code", "npx", ["-y", "@agentclientprotocol/claude-agent-acp"], "default",
        """{"claudeCode":{"options":{"disallowedTools":["Edit","Write","MultiEdit","NotebookEdit"]}}}""");

    public static readonly AcpHarness Codex = new(
        new AcpHarnessId("codex"), "Codex", "npx", ["-y", "@agentclientprotocol/codex-acp"], "read-only");

    public static readonly AcpHarness Gemini = new(
        new AcpHarnessId("gemini"), "Gemini CLI", "gemini", ["--experimental-acp"], "default");

    public static readonly IReadOnlyList<AcpHarness> BuiltIn = [ClaudeCode, Codex, Gemini];

    public static AcpHarness? Find(AcpHarnessId id)
    {
        foreach (var harness in BuiltIn)
            if (harness.Id == id)
                return harness;
        return null;
    }
}
