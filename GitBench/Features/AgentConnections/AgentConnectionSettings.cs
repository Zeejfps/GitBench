using GitBench.App;
using ZGF.Gui.Desktop;

namespace GitBench.Features.AgentConnections;

/// <summary>
/// The agent-connections preference as one value: whether the server is wanted, on which port,
/// and the path token the endpoint carries. The token is null until the first enable generates
/// one; it is then kept so a copied <c>claude mcp add</c> command stays valid across restarts.
/// </summary>
internal sealed record AgentConnectionSettings(bool Enabled, int Port, McpPathToken? Token)
{
    public const int DefaultPort = 5577;

    public static AgentConnectionSettings From(Preferences preferences) =>
        new(preferences.AgentConnectionsEnabled, preferences.AgentConnectionsPort, preferences.AgentConnectionsToken);

    /// <summary>Parses a port typed into the settings field: an integer from 1 to 65535.</summary>
    public static bool TryParsePort(string text, out int port)
    {
        if (int.TryParse(text.Trim(), out var parsed) && IsValidPort(parsed))
        {
            port = parsed;
            return true;
        }

        port = 0;
        return false;
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;
}

/// <summary>What the agent-connections server is doing, for the settings card and the status bar.</summary>
internal abstract record AgentConnectionState
{
    public sealed record Off : AgentConnectionState;

    public sealed record Listening(Uri Endpoint, int Sessions) : AgentConnectionState
    {
        /// <summary>The command that registers this endpoint with Claude Code.</summary>
        public string ClaudeMcpAddCommand => $"claude mcp add --transport http diffdino {Endpoint}";
    }

    /// <summary>The preference is on but nothing is listening; <paramref name="Reason"/> says why.</summary>
    public sealed record Failed(string Reason) : AgentConnectionState;
}
