using GitBench.Localization;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Features.LanguageServers;

internal static class ServerStateText
{
    public static string Of(ServerState state, Strings s) => state switch
    {
        ServerState.NotConfigured => s.LanguageServersNotConfigured,
        ServerState.Stopped => s.LanguageServersStatusStopped,
        ServerState.Starting => s.LanguageServersStatusStarting,
        ServerState.Indexing { PercentComplete: { } percent } =>
            s.LanguageServersStatusIndexingPercent(percent.ToString()),
        ServerState.Indexing => s.LanguageServersStatusIndexing,
        ServerState.Ready => s.LanguageServersStatusReady,
        ServerState.Failed => s.LanguageServersStatusFailed,
        _ => throw new NotSupportedException($"unhandled server state {state.GetType().Name}"),
    };

    public static string Detailed(ServerState state, Strings s) =>
        state is ServerState.Failed failed
            ? $"{Of(state, s)} — {failed.Reason}"
            : Of(state, s);

    public static bool WorthShowing(ServerState state) => state is not ServerState.NotConfigured;
}
