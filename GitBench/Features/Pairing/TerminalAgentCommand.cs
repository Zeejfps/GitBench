using GitBench.Features.Terminal;

namespace GitBench.Features.Pairing;

/// <summary>The values a terminal preset's command template is filled with.</summary>
internal sealed record TerminalAgentValues(string Prompt, string PromptFile, Uri McpUrl, string McpConfigFile, string WorkingDirectory);

/// <summary>
/// A terminal preset's command line: a template with <c>{prompt}</c>, <c>{promptFile}</c>,
/// <c>{mcpUrl}</c>, <c>{mcpConfigFile}</c> and <c>{cwd}</c>, each filled in quoted for the shell
/// that runs it — so nothing the goal says reaches the shell as syntax.
/// </summary>
internal static class TerminalAgentCommand
{
    /// <summary>Claude Code in its own terminal, handed the app's server and told not to edit files;
    /// its shell stays, to run the tests. Flags, not a guard: the CLI enforces them, the app can't.</summary>
    public const string ClaudeCode =
        "claude --mcp-config {mcpConfigFile} --disallowedTools Edit,Write,MultiEdit,NotebookEdit {prompt}";

    /// <summary>The default before the shell was given back, upgraded where a preference still holds it.</summary>
    public const string LegacyClaudeCode =
        "claude --mcp-config {mcpConfigFile} --disallowedTools Edit,Write,MultiEdit,NotebookEdit,Bash,PowerShell {prompt}";

    /// <summary>The filled command line, or null when a value can't be quoted safely for the shell
    /// (the Windows command processor has no escape for some characters).</summary>
    public static string? Build(string template, ShellFamily family, TerminalAgentValues values)
    {
        var filled = template;
        foreach (var (placeholder, value) in new[]
                 {
                     ("{prompt}", values.Prompt),
                     ("{promptFile}", values.PromptFile),
                     ("{mcpUrl}", values.McpUrl.ToString()),
                     ("{mcpConfigFile}", values.McpConfigFile),
                     ("{cwd}", values.WorkingDirectory),
                 })
        {
            if (!filled.Contains(placeholder, StringComparison.Ordinal)) continue;
            if (Quote(value, family) is not { } quoted) return null;
            filled = filled.Replace(placeholder, quoted, StringComparison.Ordinal);
        }

        return filled;
    }

    private static string? Quote(string value, ShellFamily family) => family switch
    {
        ShellFamily.Posix => ShellPathQuoting.QuotePosix(value),
        ShellFamily.PowerShell => ShellPathQuoting.QuotePowerShell(value),
        // One line, and none of the characters the command processor expands inside quotes.
        ShellFamily.CommandProcessor => value.IndexOfAny(['"', '%', '!', '^']) >= 0
            ? null
            : "\"" + value.Replace("\r", " ").Replace("\n", " ") + "\"",
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown shell family."),
    };
}
