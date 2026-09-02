using System.Text;

namespace GitBench.Lsp.Configuration;

/// <summary>
/// A server the app knows how to suggest: what it is called, what to run, and which files it would
/// answer for. Only ever a suggestion — nothing is installed, and nothing starts until the user has
/// written it into the config file.
/// </summary>
public sealed record StarterServer(
    LanguageId Language,
    string DisplayName,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyList<FileExtension> Extensions,
    IReadOnlyList<string> RootMarkers)
{
    /// <summary>
    /// What says a repository is written in this language. Separate from <see cref="RootMarkers"/>
    /// because the two answer different questions: these are matched against one directory listing
    /// and may name a kind of file, while root markers are looked up by name while walking up from
    /// a file and go into the config as written.
    /// </summary>
    public IReadOnlyList<string> DetectMarkers { get; init; } = RootMarkers;
}

/// <summary>
/// The catalogue behind the settings card: which servers exist for the languages a repository is
/// written in, and the text of a config file that would run them.
/// </summary>
/// <remarks>
/// A repository is judged by the marker files at its root rather than by counting source files.
/// A marker is the same thing that finds a project root at launch time, it is one directory
/// listing rather than a walk of the working tree, and a repository with a <c>Cargo.toml</c> is a
/// Rust project whether or not the reader has opened a <c>.rs</c> file yet.
/// </remarks>
public static class StarterServers
{
    public static IReadOnlyList<StarterServer> All { get; } =
    [
        Starter("rust", "Rust", "rust-analyzer", [], [".rs"], ["Cargo.toml"]),
        Starter("go", "Go", "gopls", [], [".go"], ["go.mod", "go.work"]),
        Starter(
            "typescript",
            "TypeScript",
            "typescript-language-server",
            ["--stdio"],
            [".ts", ".tsx", ".mts", ".cts"],
            ["tsconfig.json", "package.json"]),
        Starter("python", "Python", "pyright-langserver", ["--stdio"], [".py", ".pyi"], ["pyproject.toml", "setup.py"]),
        Starter("csharp", "C#", "csharp-ls", [], [".cs"], [], detect: ["*.sln", "*.csproj"]),
        Starter("cpp", "C/C++", "clangd", [], [".c", ".h", ".cc", ".cpp", ".hpp"], ["compile_commands.json", "CMakeLists.txt"]),
        Starter("zig", "Zig", "zls", [], [".zig"], ["build.zig"]),
    ];

    /// <summary>
    /// Servers worth offering for a repository: those whose marker files are present at its root
    /// and whose language the config does not already name. A language that is configured is not a
    /// suggestion even when its server is failing — that is a problem to show, not a gap to fill.
    /// </summary>
    /// <param name="rootEntryNames">The names — not paths — of the entries in the repository root.</param>
    public static IReadOnlyList<StarterServer> SuggestFor(
        IEnumerable<string> rootEntryNames, LanguageServerConfig configured)
    {
        var present = new HashSet<string>(rootEntryNames, StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<StarterServer>();

        foreach (var server in All)
        {
            if (configured.Mentions(server.Language)) continue;
            if (!server.DetectMarkers.Any(marker => Matches(present, marker))) continue;
            suggestions.Add(server);
        }

        return suggestions;
    }

    /// <summary>A whole config file that would run these servers, as the user would have written
    /// it. Parses back to what it describes, so what the card offers and what the app then reads
    /// cannot disagree.</summary>
    public static string ConfigText(IReadOnlyList<StarterServer> servers)
    {
        var text = new StringBuilder();
        text.Append("// GitBench language servers. Install the command, then reload from Settings.\n");
        text.Append("{\n");
        text.Append("  \"version\": 1,\n");
        text.Append("  \"servers\": {\n");

        for (var i = 0; i < servers.Count; i++)
        {
            text.Append(EntryText(servers[i], indent: "    "));
            text.Append(i == servers.Count - 1 ? "\n" : ",\n");
        }

        text.Append("  },\n");
        text.Append($"  \"maxConcurrentServers\": {LanguageServerConfig.DefaultMaxConcurrentServers}\n");
        text.Append("}\n");
        return text.ToString();
    }

    /// <summary>One server's entry, for pasting into a config file that already exists. The app
    /// never edits that file: it is hand-written, comments and all, and rewriting it would lose
    /// whatever the user put there.</summary>
    public static string EntryText(StarterServer server, string indent = "")
    {
        var text = new StringBuilder();
        text.Append($"{indent}\"{server.Language.Value}\": {{\n");
        text.Append($"{indent}  \"command\": {Quoted(server.Command)},\n");
        text.Append($"{indent}  \"args\": {List(server.Args)},\n");
        text.Append($"{indent}  \"extensions\": {List(server.Extensions.Select(e => e.Value))},\n");
        text.Append($"{indent}  \"rootMarkers\": {List(server.RootMarkers)}\n");
        text.Append($"{indent}}}");
        return text.ToString();
    }

    // A marker written with a leading star matches by extension: a C# project is named after the
    // solution, so the file to look for is not a fixed name.
    static bool Matches(HashSet<string> present, string marker) =>
        marker.StartsWith('*')
            ? present.Any(name => name.EndsWith(marker[1..], StringComparison.OrdinalIgnoreCase))
            : present.Contains(marker);

    static string List(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(Quoted)) + "]";

    static string Quoted(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static StarterServer Starter(
        string language,
        string displayName,
        string command,
        string[] args,
        string[] extensions,
        string[] rootMarkers,
        string[]? detect = null) =>
        new(
            LanguageId.Of(language),
            displayName,
            command,
            args,
            extensions.Select(Extension).ToArray(),
            rootMarkers)
        {
            DetectMarkers = detect ?? rootMarkers,
        };

    static FileExtension Extension(string raw) =>
        FileExtension.TryParse(raw, out var extension)
            ? extension
            : throw new ArgumentException($"'{raw}' is not a file extension.", nameof(raw));
}
