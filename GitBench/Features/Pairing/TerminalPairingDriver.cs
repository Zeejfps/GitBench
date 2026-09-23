using System.Text.Json.Nodes;
using GitBench.App;
using GitBench.Features.AgentConnections;
using GitBench.Features.Terminal;
using GitBench.Git;
using ZGF.Observable;

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

/// <summary>
/// Runs a session's agent from a terminal preset: the command, filled with the opening prompt and
/// the app's MCP server, starts in a terminal tab of the repository. The loop's tools are the same
/// MCP tools, so the stops work unchanged; what is weaker is said up front — no
/// write is refused by the app, and what the agent says stays in the terminal.
/// </summary>
internal sealed class TerminalPairingDriver : IAsyncDisposable
{
    private readonly PairingStore _store;
    private readonly Repo _repo;
    private readonly string _label;
    private readonly string _template;
    private readonly AgentEndpoints _endpoints;
    private readonly ITerminalSessionStore _terminals;
    private readonly CommandLaunchFactory _launches;
    private readonly IContentNavigator _navigator;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"diffdino-pairing-{Guid.NewGuid():N}");
    private IDisposable? _watch;
    private TerminalInstance? _terminal;

    private TerminalPairingDriver(
        PairingStore store, Repo repo, string label, string template, AgentEndpoints endpoints,
        ITerminalSessionStore terminals, CommandLaunchFactory launches, IContentNavigator navigator, IUiDispatcher dispatcher)
    {
        _store = store;
        _repo = repo;
        _label = label;
        _template = template;
        _endpoints = endpoints;
        _terminals = terminals;
        _launches = launches;
        _navigator = navigator;
        _dispatcher = dispatcher;
    }

    /// <summary>Starts the agent's terminal for a session. UI thread.</summary>
    public static TerminalPairingDriver Start(
        PairingStore store, Repo repo, string label, string template, AgentEndpoints endpoints,
        ITerminalSessionStore terminals, CommandLaunchFactory launches, IContentNavigator navigator, IUiDispatcher dispatcher)
    {
        var driver = new TerminalPairingDriver(store, repo, label, template, endpoints, terminals, launches, navigator, dispatcher);
        _ = driver.RunAsync();
        return driver;
    }

    // Starts on the UI thread and does not come back to it: every touch of the store and the
    // terminals after the first await is posted.
    private async Task RunAsync()
    {
        Uri url;
        try
        {
            switch (await _endpoints.EnsureAsync(_stop.Token))
            {
                case AgentEndpoint.Listening listening:
                    url = listening.Url;
                    break;
                case AgentEndpoint.Unavailable unavailable:
                    Post(() => _store.Fail($"DiffDino's agent connections server is not available: {unavailable.Reason}"));
                    return;
                default:
                    throw new InvalidOperationException("Unhandled endpoint.");
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var prompt = PairingInstructions.Opening(_store.Goal, _repo.Path);
        var promptFile = Path.Combine(_scratch, "prompt.md");
        var configFile = Path.Combine(_scratch, "mcp.json");
        try
        {
            Directory.CreateDirectory(_scratch);
            await File.WriteAllTextAsync(promptFile, prompt);
            await File.WriteAllTextAsync(configFile, new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["diffdino"] = new JsonObject { ["type"] = "http", ["url"] = url.ToString() },
                },
            }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Post(() => _store.Fail($"The agent's files could not be written: {e.Message}"));
            return;
        }

        Post(() => Launch(new TerminalAgentValues(prompt, promptFile, url, configFile, _repo.Path)));
    }

    private void Launch(TerminalAgentValues values)
    {
        if (_stop.IsCancellationRequested) return;
        if (TerminalAgentCommand.Build(_template, ShellCommand.Family, values) is not { } command)
        {
            _store.Fail("The command template can't be filled safely for this shell. Use {promptFile} instead of {prompt}.");
            return;
        }

        var terminal = _terminals.StartIn(_repo, _launches(_label, _repo.Path, command));
        _terminal = terminal;
        _navigator.Show(new ContentPlace.Shell(terminal));
        _store.MarkRunning();
        _store.AddNotice(
            $"{_label} runs in a terminal with no enforced write guard: keeping it read-only is up to the command's own flags.",
            NoticeTone.Refused);
        _watch = terminal.Render.Subscribe(state =>
        {
            if (state is TerminalRenderState.Exited or TerminalRenderState.Failed or TerminalRenderState.Faulted)
                _store.MarkDisconnected($"{_label}'s terminal exited.");
        });
    }

    private void Post(Action action) => _dispatcher.Post(action);

    /// <summary>Ends the agent with its terminal, so it can't drive the repository's next session.
    /// UI thread.</summary>
    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _watch?.Dispose();
        if (_terminal is { } terminal) _terminals.CloseIn(_repo, terminal);
        try
        {
            if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}
