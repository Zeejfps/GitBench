using System.Text.Json;
using GitBench.App;
using GitBench.Features.Pairing;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The terminal pairing command as a stored preference. The old default took the agent's shell
/// away; a preference still holding it word for word is upgraded, a command the user wrote is kept.
/// </summary>
public sealed class PairingTerminalCommandPreferenceTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-pairing-command-prefs-");

    public void Dispose() => _dir.Dispose();

    private string WriteFile(string command)
    {
        var path = Path.Combine(_dir.Path, "prefs.json");
        File.WriteAllText(path, $$"""
        {
          "schemaVersion": 1,
          "pairingTerminalCommand": {{JsonSerializer.Serialize(command)}}
        }
        """);
        return path;
    }

    [Fact]
    public void TheOldDefault_IsUpgraded_SoTheAgentCanRunTests()
    {
        var loaded = PreferencesStore.Load(WriteFile(TerminalAgentCommand.LegacyClaudeCode));

        Assert.Equal(TerminalAgentCommand.ClaudeCode, loaded.PairingTerminalCommand);
        Assert.DoesNotContain("Bash", loaded.PairingTerminalCommand);
    }

    [Fact]
    public void ACommandTheUserWrote_IsKept()
    {
        const string own = "claude --mcp-config {mcpConfigFile} --disallowedTools Bash {prompt}";

        Assert.Equal(own, PreferencesStore.Load(WriteFile(own)).PairingTerminalCommand);
    }
}
