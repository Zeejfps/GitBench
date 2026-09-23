using ZGF.Observable;

namespace GitBench.Features.AgentConnections;

/// <summary>Where an agent the app starts itself reaches the app's MCP server.</summary>
internal abstract record AgentEndpoint
{
    public sealed record Listening(Uri Url) : AgentEndpoint;

    public sealed record Unavailable(string Reason) : AgentEndpoint;
}

/// <summary>
/// Hands out the agent-connections endpoint to an agent the app starts itself, turning the server
/// on when it is off — the agent has no other way to reach the app — and waiting for it to listen.
/// UI thread only.
/// </summary>
internal sealed class AgentEndpoints
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    private readonly State<AgentConnectionSettings> _settings;
    private readonly IReadable<AgentConnectionState> _state;
    private readonly IUiDispatcher _dispatcher;

    public AgentEndpoints(State<AgentConnectionSettings> settings, IReadable<AgentConnectionState> state, IUiDispatcher dispatcher)
    {
        _settings = settings;
        _state = state;
        _dispatcher = dispatcher;
    }

    /// <summary>Whether asking will switch the server on.</summary>
    public bool WillEnable => !_settings.Value.Enabled;

    public async Task<AgentEndpoint> EnsureAsync(CancellationToken ct)
    {
        if (_state.Value is AgentConnectionState.Listening listening) return new AgentEndpoint.Listening(listening.Endpoint);
        if (!_settings.Value.Enabled) _settings.Value = _settings.Value with { Enabled = true };

        var settled = new TaskCompletionSource<AgentEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = _state.Subscribe(state =>
        {
            switch (state)
            {
                case AgentConnectionState.Listening now:
                    settled.TrySetResult(new AgentEndpoint.Listening(now.Endpoint));
                    break;
                case AgentConnectionState.Failed failed:
                    settled.TrySetResult(new AgentEndpoint.Unavailable(failed.Reason));
                    break;
                case AgentConnectionState.Off:
                    break;
                default:
                    throw new InvalidOperationException("Unhandled agent connection state.");
            }
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartTimeout);
        try
        {
            return await settled.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AgentEndpoint.Unavailable("The agent connections server did not start.");
        }
        finally
        {
            // The wait may finish on any thread; the state is the UI thread's.
            _dispatcher.Post(subscription.Dispose);
        }
    }
}
