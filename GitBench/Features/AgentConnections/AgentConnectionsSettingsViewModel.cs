using System.Globalization;
using GitBench.Features.Notifications;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.AgentConnections;

/// <summary>
/// The agent-connections settings as the card edits them: the on/off toggle and the port draft
/// write through to the preference, the server's state reads back as one line, and the copied
/// <c>claude mcp add</c> command is the endpoint's own.
/// </summary>
/// <remarks>
/// The port is parsed on every edit: a draft that reads as a port is saved at once, one that does
/// not shows the error and saves nothing. A saved port only overwrites the draft when the
/// preference moved under it, so an edit in progress is never reset by its own commit.
/// </remarks>
internal sealed class AgentConnectionsSettingsViewModel : IDisposable
{
    private readonly State<AgentConnectionSettings> _settings;
    private readonly IReadable<AgentConnectionState> _state;
    private readonly IClipboard _clipboard;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly IDisposable _settingsSubscription;
    private readonly Derived<string> _statusText;
    private readonly Derived<bool> _canCopy;
    private int _committedPort;

    public AgentConnectionsSettingsViewModel(
        State<AgentConnectionSettings> settings,
        IReadable<AgentConnectionState> state,
        IClipboard clipboard,
        IMessageBus bus,
        ILocalizationService loc)
    {
        _settings = settings;
        _state = state;
        _clipboard = clipboard;
        _bus = bus;
        _loc = loc;
        _committedPort = settings.Value.Port;
        Enabled = new State<bool>(settings.Value.Enabled);
        PortDraft = new State<string>(PortText(settings.Value.Port));
        _statusText = new Derived<string>(() => Describe(loc.Strings.Value, state.Value));
        _canCopy = new Derived<bool>(() => state.Value is AgentConnectionState.Listening);
        CopyCommand = new Command(Copy, _canCopy);

        Enabled.Changed += enabled => _settings.Value = _settings.Value with { Enabled = enabled };
        PortDraft.Changed += OnPortDraft;
        _settingsSubscription = settings.Subscribe(OnSettings);
    }

    public State<bool> Enabled { get; }

    /// <summary>The port field's text, which may not be a port yet.</summary>
    public State<string> PortDraft { get; }

    /// <summary>True while <see cref="PortDraft"/> does not read as a port; nothing is saved then.</summary>
    public State<bool> PortInvalid { get; } = new(false);

    /// <summary>"Off", where the server listens, or why it does not.</summary>
    public IReadable<string> StatusText => _statusText;

    public IReadable<bool> CanCopy => _canCopy;

    /// <summary>Copies the <c>claude mcp add</c> command for the live endpoint and says so.</summary>
    public Command CopyCommand { get; }

    private void OnSettings(AgentConnectionSettings settings)
    {
        Enabled.Value = settings.Enabled;
        if (settings.Port == _committedPort) return;
        _committedPort = settings.Port;
        PortDraft.Value = PortText(settings.Port);
        PortInvalid.Value = false;
    }

    private void OnPortDraft(string text)
    {
        if (!AgentConnectionSettings.TryParsePort(text, out var port))
        {
            PortInvalid.Value = true;
            return;
        }

        PortInvalid.Value = false;
        if (port == _committedPort) return;
        _committedPort = port;
        _settings.Value = _settings.Value with { Port = port };
    }

    private void Copy()
    {
        if (_state.Value is not AgentConnectionState.Listening listening) return;
        _clipboard.SetText(listening.ClaudeMcpAddCommand);
        _bus.Broadcast(new ShowToastMessage(ToastIntent.Success(_loc.Strings.Value.SettingsAgentConnectionsCommandCopied)));
    }

    private static string Describe(Strings strings, AgentConnectionState state) => state switch
    {
        AgentConnectionState.Off => strings.SettingsAgentConnectionsOff,
        AgentConnectionState.Listening listening => strings.SettingsAgentConnectionsListening(listening.Endpoint.ToString()),
        AgentConnectionState.Failed failed => strings.SettingsAgentConnectionsFailed(failed.Reason),
        _ => throw new InvalidOperationException($"Unhandled state {state.GetType().Name}."),
    };

    private static string PortText(int port) => port.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _settingsSubscription.Dispose();
        _canCopy.Dispose();
        _statusText.Dispose();
        Enabled.Dispose();
        PortDraft.Dispose();
        PortInvalid.Dispose();
    }
}
