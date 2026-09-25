using McpSdk.Server;
using ZGF.Gui.Desktop;
using ZGF.Observable;

namespace GitBench.Features.AgentConnections;

/// <summary>The one MCP server slot the app has, as the service drives it.</summary>
internal interface IMcpServerHost
{
    McpServerStart Start(McpServerOptions options);

    void Stop();
}

/// <summary>Drives <see cref="GuiApp"/>'s server. UI thread only, like the app's own members.</summary>
internal sealed class GuiAppMcpServerHost : IMcpServerHost
{
    private readonly GuiApp _app;

    public GuiAppMcpServerHost(GuiApp app) => _app = app;

    public McpServerStart Start(McpServerOptions options) => _app.StartMcpServer(options);

    public void Stop() => _app.StopMcpServer();
}

/// <summary>
/// Keeps the agent-connections server in step with its preference: on, on the chosen port or the
/// next free one after it, behind the stored token — generating that token the first time the
/// preference is enabled — and off otherwise. Publishes what it is doing, with the open session count, for the settings card and
/// the status bar. UI thread only.
/// </summary>
/// <remarks>
/// The <c>ZGF_GUI_MCP</c> debug server has its own slot in the app, so a scripted run drives the
/// window on one port while agents connect on another. A start that nevertheless finds a server
/// in the app's slot reports <see cref="AgentConnectionState.Failed"/> naming its endpoint rather
/// than taking it over, and disabling the preference never stops a server this service did not start.
/// </remarks>
internal sealed class AgentConnectionService : IDisposable
{
    public const string ServerName = "DiffDino";
    public const int PortAttempts = 20;

    private readonly State<AgentConnectionSettings> _settings;
    private readonly State<AgentConnectionState> _state;
    private readonly AgentToolMcpSource _source;
    private readonly IPromptController _prompts;
    private readonly IMcpServerHost _host;
    private readonly IDisposable _settingsSubscription;
    private readonly IDisposable _sessionsSubscription;

    private Server _server = new Server.Stopped();

    public AgentConnectionService(
        State<AgentConnectionSettings> settings,
        State<AgentConnectionState> state,
        AgentToolMcpSource source,
        IPromptController prompts,
        IMcpServerHost host)
    {
        _settings = settings;
        _state = state;
        _source = source;
        _prompts = prompts;
        _host = host;
        _settingsSubscription = settings.Subscribe(Apply);
        _sessionsSubscription = source.OpenSessions.Subscribe(OnSessionsChanged);
    }

    public IReadable<AgentConnectionState> State => _state;

    private void Apply(AgentConnectionSettings settings)
    {
        if (!settings.Enabled)
        {
            StopIfRunning();
            _state.Value = new AgentConnectionState.Off();
            return;
        }

        if (settings.Token is not { } token)
        {
            // Writing the token back re-enters this handler with the complete settings, which is
            // the call that starts the server; nothing else is left to do on this pass.
            _settings.Value = settings with { Token = McpPathToken.Generate() };
            return;
        }

        if (_server is Server.Running running && running.Port == settings.Port && running.Token == token)
            return;

        StopIfRunning();
        _state.Value = Start(settings.Port, token);
    }

    // A port another instance holds moves the server up to the next free one; the preference keeps
    // the chosen port, and the state reports where the server actually listens.
    private AgentConnectionState Start(int port, McpPathToken token)
    {
        McpServerStart.Failed? firstFailure = null;
        var last = Math.Min(port + PortAttempts - 1, 65535);
        for (var candidate = port; candidate <= last; candidate++)
        {
            var options = new McpServerOptions
            {
                ServerName = ServerName,
                Port = candidate,
                PathToken = token,
                Instructions = AgentConnectionInstructions.Text,
                Prompts = _prompts,
                ToolSources = [_source],
                IncludeGuiTools = false,
            };
            switch (_host.Start(options))
            {
                case McpServerStart.Started started:
                    _server = new Server.Running(port, token);
                    return new AgentConnectionState.Listening(started.Endpoint, _source.OpenSessions.Value);
                case McpServerStart.AlreadyRunning already:
                    return new AgentConnectionState.Failed(
                        $"Another MCP server is already listening at {already.Endpoint}. Agent connections are off while it runs.");
                case McpServerStart.Failed failed:
                    firstFailure ??= failed;
                    break;
                default:
                    throw new InvalidOperationException("Unhandled server start outcome.");
            }
        }

        return new AgentConnectionState.Failed(firstFailure!.Message);
    }

    private void StopIfRunning()
    {
        if (_server is not Server.Running) return;
        _host.Stop();
        _server = new Server.Stopped();
    }

    private void OnSessionsChanged(int sessions)
    {
        if (_state.Value is AgentConnectionState.Listening listening && listening.Sessions != sessions)
            _state.Value = listening with { Sessions = sessions };
    }

    public void Dispose()
    {
        _settingsSubscription.Dispose();
        _sessionsSubscription.Dispose();
    }

    private abstract record Server
    {
        public sealed record Stopped : Server;

        /// <summary>A server this service started, and what it was started with.</summary>
        public sealed record Running(int Port, McpPathToken Token) : Server;
    }
}
