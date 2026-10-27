using System.Text.Json.Nodes;
using GitBench.App;
using GitBench.Features.AgentConnections;
using GitBench.Features.Terminal;
using GitBench.Git;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>
/// Runs a conversation's agent from a terminal preset: the command, filled with the opening prompt
/// and the app's MCP server, starts in a terminal tab of the repository. The loop's tools are the
/// same MCP tools, so the stops work unchanged; what is weaker is said up front — no write is
/// refused by the app, and what the agent says stays in the terminal, which is also where the user
/// talks to it between sessions.
/// </summary>
internal sealed class TerminalPairingDriver : IAgentDriver
{
    private readonly AgentConversation _conversation;
    private readonly AgentPrompt _opening;
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
    private int _told;

    private TerminalPairingDriver(
        AgentConversation conversation, AgentPrompt opening, string label, string template, AgentEndpoints endpoints,
        ITerminalSessionStore terminals, CommandLaunchFactory launches, IContentNavigator navigator, IUiDispatcher dispatcher)
    {
        _conversation = conversation;
        _opening = opening;
        _repo = conversation.Repo;
        _label = label;
        _template = template;
        _endpoints = endpoints;
        _terminals = terminals;
        _launches = launches;
        _navigator = navigator;
        _dispatcher = dispatcher;
    }

    /// <summary>Starts the agent's terminal for a conversation, with <paramref name="opening"/> as
    /// its first prompt. UI thread.</summary>
    public static TerminalPairingDriver Start(
        AgentConversation conversation, AgentPrompt opening, string label, string template, AgentEndpoints endpoints,
        ITerminalSessionStore terminals, CommandLaunchFactory launches, IContentNavigator navigator, IUiDispatcher dispatcher)
    {
        var driver = new TerminalPairingDriver(conversation, opening, label, template, endpoints, terminals, launches, navigator, dispatcher);
        _ = driver.RunAsync();
        return driver;
    }

    // The prompt goes in a file and the terminal is handed one line naming it: typed into a CLI
    // mid-conversation, a many-line prompt would be sent line by line.
    public void Tell(AgentPrompt prompt)
    {
        if (_terminal is not { } terminal || _stop.IsCancellationRequested) return;
        var file = Path.Combine(_scratch, $"prompt-{++_told}.md");
        try
        {
            File.WriteAllText(file, prompt.ToMarkdown(_repo.Path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _conversation.Transcript.AddNotice($"The prompt for {_label} could not be written: {e.Message}", NoticeTone.Error);
            return;
        }

        terminal.Paste($"Read {file} and follow it.");
        terminal.SendInput("\r"u8);
        _navigator.Show(new ContentPlace.Shell(terminal));
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
                    Post(() => _conversation.Fail($"DiffDino's agent connections server is not available: {unavailable.Reason}"));
                    return;
                default:
                    throw new InvalidOperationException("Unhandled endpoint.");
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var prompt = _opening.ToMarkdown(_repo.Path);
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
            Post(() => _conversation.Fail($"The agent's files could not be written: {e.Message}"));
            return;
        }

        Post(() => Launch(new TerminalAgentValues(prompt, promptFile, url, configFile, _repo.Path)));
    }

    private void Launch(TerminalAgentValues values)
    {
        if (_stop.IsCancellationRequested) return;
        if (TerminalAgentCommand.Build(_template, ShellCommand.Family, values) is not { } command)
        {
            _conversation.Fail("The command template can't be filled safely for this shell. Use {promptFile} instead of {prompt}.");
            return;
        }

        var terminal = _terminals.StartIn(_repo, _launches(_label, _repo.Path, command));
        _terminal = terminal;
        _navigator.Show(new ContentPlace.Shell(terminal));
        _conversation.MarkRunning();
        _conversation.Transcript.AddNotice(
            $"{_label} runs in a terminal with no enforced write guard: keeping it read-only is up to the command's own flags.",
            NoticeTone.Refused);
        _watch = terminal.Render.Subscribe(state =>
        {
            if (state is TerminalRenderState.Exited or TerminalRenderState.Failed or TerminalRenderState.Faulted)
                _conversation.Disconnect($"{_label}'s terminal exited.");
        });
    }

    private void Post(Action action) => _dispatcher.Post(action);

    /// <summary>Ends the agent with its terminal, so it can't drive the repository's next
    /// conversation. UI thread.</summary>
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
