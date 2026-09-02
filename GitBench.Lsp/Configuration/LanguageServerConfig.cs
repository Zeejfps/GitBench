namespace GitBench.Lsp.Configuration;

/// <summary>
/// One server the user configured: everything needed to launch it and to decide which files it
/// answers for. Built only by the parser, so every field here has already been checked.
/// </summary>
public sealed record LanguageServerEntry(
    LanguageId Language,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyList<FileExtension> Extensions,
    IReadOnlyList<string> RootMarkers,
    IReadOnlyDictionary<string, string> Environment,
    string? InitializationOptionsJson,
    string? SettingsJson,
    TimeSpan RequestTimeout,
    TimeSpan IdleShutdown)
{
    /// <summary>
    /// Whether a server already running under <paramref name="other"/> would be the same process
    /// if launched from this entry. Only the launch-affecting fields count: editing a timeout in
    /// the config should not throw away a warm rust-analyzer.
    /// </summary>
    public bool SameLaunchAs(LanguageServerEntry other) =>
        Command == other.Command &&
        Args.SequenceEqual(other.Args) &&
        RootMarkers.SequenceEqual(other.RootMarkers) &&
        Environment.Count == other.Environment.Count &&
        Environment.All(kv => other.Environment.TryGetValue(kv.Key, out var v) && v == kv.Value) &&
        InitializationOptionsJson == other.InitializationOptionsJson &&
        SettingsJson == other.SettingsJson;
}

/// <summary>The usable content of the config file: which servers exist, and how many may run.</summary>
public sealed record LanguageServerConfig(
    IReadOnlyList<LanguageServerEntry> Servers,
    int MaxConcurrentServers)
{
    public const int DefaultMaxConcurrentServers = 2;

    /// <summary>
    /// Languages the file names and switches off. Not servers — nothing here can be launched — but
    /// not absent either: a language the user turned off is one they have already decided about,
    /// and offering to configure it again would be reading their config back to them.
    /// </summary>
    public IReadOnlyList<LanguageId> Disabled { get; init; } = [];

    public static readonly LanguageServerConfig Empty =
        new([], DefaultMaxConcurrentServers);

    /// <summary>The server that answers for a file, or none if no configured server claims it.</summary>
    public LanguageServerEntry? ServerFor(string filePath)
    {
        if (FileExtension.Of(filePath) is not { } extension) return null;
        foreach (var server in Servers)
            if (server.Extensions.Contains(extension))
                return server;
        return null;
    }

    /// <summary>Whether the file has anything to say about a language, running or not.</summary>
    public bool Mentions(LanguageId language) =>
        ServerFor(language) is not null || Disabled.Contains(language);

    public LanguageServerEntry? ServerFor(LanguageId language)
    {
        foreach (var server in Servers)
            if (server.Language == language)
                return server;
        return null;
    }

    public static ConfigParse Parse(string text) => LanguageServerConfigParser.Parse(text);
}

/// <summary>
/// The result of reading the one file in the app a user writes by hand. A file that cannot be used
/// at all and a file with one bad entry are different things, so they are different cases.
/// </summary>
public abstract record ConfigParse
{
    ConfigParse() { }

    /// <param name="Problems">
    /// What was skipped and why. The config is still usable; these are shown, not thrown.
    /// </param>
    public sealed record Loaded(LanguageServerConfig Config, IReadOnlyList<ConfigProblem> Problems) : ConfigParse;

    /// <summary>The text is not a config file — a syntax error, or JSON of the wrong shape.</summary>
    public sealed record NotUnderstood(ConfigError Error) : ConfigParse;

    /// <summary>The file declares a schema this build does not know how to read.</summary>
    public sealed record Unsupported(int FileVersion, int HighestSupported) : ConfigParse;
}

/// <param name="Line">1-based line of the offending text, when the failure has a position.</param>
public sealed record ConfigError(string Message, int? Line = null, int? Column = null);

/// <param name="Subject">What was skipped — a language key, or a file-level field name.</param>
public sealed record ConfigProblem(string Subject, string Message);
