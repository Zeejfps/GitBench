using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Features.LanguageServers;

internal sealed record LanguageServerSnapshot(
    LanguageServerConfig Config,
    IReadOnlyList<ServerStatus> Servers,
    IReadOnlyList<ConfigProblem> Problems,
    IReadOnlyList<StarterServer> Suggestions,
    bool ConfigFileExists)
{
    public static readonly LanguageServerSnapshot Nothing =
        new(LanguageServerConfig.Empty, [], [], [], ConfigFileExists: false);

    public bool Handles(string absolutePath) => Config.ServerFor(absolutePath) is not null;

    public ServerState StateFor(string absolutePath) =>
        Config.ServerFor(absolutePath) is { } entry
            ? StateFor(entry.Language)
            : new ServerState.NotConfigured();

    public ServerState StateFor(LanguageId language)
    {
        foreach (var status in Servers)
            if (status.Language == language)
                return status.State;

        return Config.ServerFor(language) is null
            ? new ServerState.NotConfigured()
            : new ServerState.Stopped();
    }

    public IReadOnlyList<ConfiguredServer> Configured =>
        Config.Servers.Select(entry => new ConfiguredServer(entry, StateFor(entry.Language))).ToArray();
}

internal sealed record ConfiguredServer(LanguageServerEntry Entry, ServerState State)
{
    public bool IsRunning => State is ServerState.Starting or ServerState.Indexing or ServerState.Ready;
}
