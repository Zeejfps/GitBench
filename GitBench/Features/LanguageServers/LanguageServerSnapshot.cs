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

    /// <summary>
    /// Compared by what it says rather than by the lists it was built from, which the generated
    /// equality would compare by reference. Every publish rebuilds the server list, so without this
    /// a snapshot identical in every value still reads as a change — and asking a server a question
    /// publishes one, which made every watcher redo its work once per question.
    /// </summary>
    public bool Equals(LanguageServerSnapshot? other) =>
        other is not null &&
        ConfigFileExists == other.ConfigFileExists &&
        Config == other.Config &&
        Servers.SequenceEqual(other.Servers) &&
        Problems.SequenceEqual(other.Problems) &&
        Suggestions.SequenceEqual(other.Suggestions);

    public override int GetHashCode() =>
        HashCode.Combine(Config, Servers.Count, Problems.Count, Suggestions.Count, ConfigFileExists);

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
