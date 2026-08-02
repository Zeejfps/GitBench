using System.Text.Json;
using GitBench.Git;

namespace GitBench.Features.Assistant.Tools;

/// <summary>
/// Turns a name, a fragment, a glob or a near-miss into the repo-relative paths that actually exist,
/// so a read does not fail on a path the model could not have known.
/// </summary>
/// <remarks>
/// <see cref="ReadFileTool"/> takes exact repo-relative paths and nothing else, which is right for
/// reading and useless for guessing: a file named in a diff header, a class the model knows by name
/// only, a path typed one letter wrong all fail the same way. This is the lookup step in front of
/// it. Only paths — content, and therefore any decision about what a file may reveal, stays with
/// read_file and <see cref="RepoFileGuard"/>. Credential-shaped names are dropped here anyway, so a
/// search cannot be used to enumerate what the reads refuse.
/// </remarks>
internal sealed class FindFilesTool : IAssistantTool
{
    public const int DefaultLimit = 30;
    public const int MaxLimit = 200;

    private readonly IGitRepositoryReader _git;
    private readonly Repo _repo;

    public FindFilesTool(IGitRepositoryReader git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "find_files";

    public string Description =>
        "Finds tracked files by path, file name, glob or approximate name — the lookup to run "
        + "before read_file or get_diff when you do not have an exact repo-relative path. A bare "
        + "name finds the file wherever it sits ('AgentCatalog.cs'), a fragment matches anywhere in "
        + "the path ('Assistant/Tools'), '*' and '?' glob across directories ('**' is unnecessary: "
        + "'*.csproj', 'src/*Tool.cs'), and a misspelling still ranks the file it meant. Returns "
        + "repo-relative paths, best match first, and nothing else — pass one to read_file for "
        + "content.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"pattern":{"type":"string","description":"File name, path fragment, or glob using * and ?. Case-insensitive; near-misses still match."},"limit":{"type":"integer","description":"How many paths to return (1-200, default 30)."}},"required":["pattern"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var pattern = ToolJson.String(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(ToolInvocation.Error("Argument 'pattern' is required."));

        var limit = ToolJson.Int(args, "limit", DefaultLimit, 1, MaxLimit);
        var tracked = RepoFileGuard.Searchable(_git.ListTrackedFiles(_repo));
        // One extra, so "more exist" can be reported without ranking the whole repository twice.
        var matches = PathSearch.Rank(tracked, pattern, limit + 1);
        var truncated = matches.Count > limit;
        if (truncated) matches = matches.Take(limit).ToArray();

        return Task.FromResult(ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteString("pattern", pattern.Trim());
            writer.WriteNumber("tracked_files", tracked.Count);
            writer.WritePropertyName("matches");
            writer.WriteStartArray();
            foreach (var match in matches)
                writer.WriteStringValue(match.Path);
            writer.WriteEndArray();
            writer.WriteBoolean("truncated", truncated);
        })));
    }
}
