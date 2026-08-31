using System.Text.Json;
using GitBench.Features.Branches;
using GitBench.Features.CodeIntel;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.LocalChanges;
using GitBench.Git;

namespace GitBench.Features.Assistant.Tools;

/// The read-only surface over one repository: status, working-tree changes, diffs, history,
/// commit details and branches. Every tool is a thin wrapper over <see cref="IGitService"/>.
internal static class ReadTools
{
    // Enough of a diff for the model to reason about without flooding the turn's token budget.
    public const int DiffLineCap = 1500;

    public static IReadOnlyList<IAssistantTool> CreateAll(IGitService git, Repo repo, ISymbolExtractor extractor) =>
    [
        new GetStatusTool(git, repo),
        new GetLocalChangesTool(git, repo),
        new GetDiffTool(git, repo, extractor),
        new GetCommitHistoryTool(git, repo),
        new GetCommitDetailsTool(git, repo),
        new GetBranchesTool(git, repo),
    ];

    internal static string ShortSha(string sha) => sha.Length <= 12 ? sha : sha[..12];

    /// <summary>Turns a git read into a tool result: the failure it reported, or JSON written from
    /// the value it loaded.</summary>
    internal static Task<ToolInvocation> Project<T>(
        Fetched<T> fetched,
        Action<Utf8JsonWriter, T> write,
        string emptyMessage) =>
        Task.FromResult(fetched switch
        {
            Fetched<T>.Failed failed => ToolInvocation.Error(failed.Message),
            Fetched<T>.Ok ok => ToolInvocation.Ok(ToolJson.Write(writer => write(writer, ok.Value))),
            _ => ToolInvocation.Error(emptyMessage),
        });

    internal static string StatusName(FileChangeStatus status) => status switch
    {
        FileChangeStatus.Added => "added",
        FileChangeStatus.Modified => "modified",
        FileChangeStatus.Deleted => "deleted",
        FileChangeStatus.Renamed => "renamed",
        FileChangeStatus.Copied => "copied",
        FileChangeStatus.TypeChanged => "type_changed",
        FileChangeStatus.Unmodified => "unmodified",
        FileChangeStatus.Conflicted => "conflicted",
        FileChangeStatus.Submodule => "submodule",
        _ => "unknown",
    };

    internal static void WriteFiles(Utf8JsonWriter writer, string name, IReadOnlyList<FileChange> files)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var file in files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            if (file.OldPath is { Length: > 0 })
                writer.WriteString("old_path", file.OldPath);
            writer.WriteString("status", StatusName(file.Status));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>A diff's path, kind and hunks, capped at <see cref="DiffLineCap"/> emitted lines.
    /// Shared so a review diff and a working-tree diff read identically to the model. Hunk headers
    /// come from <paramref name="annotations"/> where it can name the enclosing declaration, so the
    /// model reads the same header the diff view shows rather than git's xfuncname guess.</summary>
    internal static void WriteDiffBody(Utf8JsonWriter writer, DiffResult diff, DiffAnnotations? annotations)
    {
        writer.WriteString("path", diff.Path);
        if (diff.OldPath is { Length: > 0 })
            writer.WriteString("old_path", diff.OldPath);
        writer.WriteString("side", diff.Side.ToString().ToLowerInvariant());
        writer.WriteBoolean("binary", diff.IsBinary);
        writer.WriteBoolean("mode_only", diff.IsModeOnly);

        var emitted = 0;
        var capped = false;
        writer.WritePropertyName("hunks");
        writer.WriteStartArray();
        foreach (var hunk in diff.Hunks)
        {
            if (emitted >= DiffLineCap)
            {
                capped = true;
                break;
            }

            writer.WriteStartObject();
            writer.WriteString(
                "header",
                annotations?.HunkHeader(hunk)
                ?? hunk.Header
                ?? $"@@ -{hunk.OldStart},{hunk.OldLines} +{hunk.NewStart},{hunk.NewLines} @@");
            writer.WritePropertyName("lines");
            writer.WriteStartArray();
            foreach (var line in hunk.Lines)
            {
                if (emitted >= DiffLineCap)
                {
                    capped = true;
                    break;
                }

                var prefix = line.Kind switch
                {
                    DiffLineKind.Added => '+',
                    DiffLineKind.Removed => '-',
                    _ => ' ',
                };
                writer.WriteStringValue(prefix + line.Text);
                emitted++;
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("truncated", diff.Truncated || capped);
        WriteDeclarations(writer, diff, annotations);
    }

    /// <summary>
    /// The declarations the change touched, flattened. A model asked "what does this commit do"
    /// otherwise has to infer it from line offsets, and a capped hunk list may not even contain the
    /// lines it would have to infer from.
    /// </summary>
    private static void WriteDeclarations(Utf8JsonWriter writer, DiffResult diff, DiffAnnotations? annotations)
    {
        var changes = SymbolChangeSet.Build(diff, annotations);
        if (changes.Count == 0) return;

        writer.WritePropertyName("declarations_changed");
        writer.WriteStartArray();
        WriteDeclarations(writer, changes);
        writer.WriteEndArray();
    }

    private static void WriteDeclarations(Utf8JsonWriter writer, IReadOnlyList<SymbolChange> changes)
    {
        foreach (var change in changes)
        {
            // Unchanged entries are only in the tree to keep a changed descendant reachable; the
            // path on each entry already carries what contains it, so flattening loses nothing.
            if (change.Change != SymbolChangeKind.Unchanged)
            {
                writer.WriteStartObject();
                writer.WriteString("name", change.Path);
                writer.WriteString("kind", change.Symbol.ToString().ToLowerInvariant());
                writer.WriteString("change", change.Change.ToString().ToLowerInvariant());
                writer.WriteEndObject();
            }

            WriteDeclarations(writer, change.Children);
        }
    }

    internal static void WriteSummary(Utf8JsonWriter writer, GitStatusSummary summary)
    {
        if (summary.Branch is { Length: > 0 } branch)
            writer.WriteString("branch", branch);
        else
            writer.WriteNull("branch");
        writer.WriteBoolean("detached", summary.IsDetached);
        writer.WriteBoolean("has_upstream", summary.HasUpstream);
        writer.WriteNumber("ahead", summary.Ahead);
        writer.WriteNumber("behind", summary.Behind);
        writer.WriteBoolean("dirty", summary.IsDirty);
    }
}

internal sealed class GetStatusTool : IAssistantTool
{
    private readonly IGitStatusReader _git;
    private readonly Repo _repo;

    public GetStatusTool(IGitStatusReader git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "get_status";

    public string Description =>
        "Current branch, whether HEAD is detached, upstream tracking with ahead/behind counts, "
        + "and whether the working tree has uncommitted changes. Cheap — call it first when you "
        + "need to orient yourself.";

    public string JsonSchema => """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var summary = _git.GetStatusSummary(_repo);
        if (summary is null)
            return Task.FromResult(ToolInvocation.Error("The status probe failed; the repository state is unknown."));

        var json = ToolJson.Write(writer => ReadTools.WriteSummary(writer, summary));
        return Task.FromResult(ToolInvocation.Ok(json));
    }
}

internal sealed class GetLocalChangesTool : IAssistantTool
{
    private readonly IGitStatusReader _git;
    private readonly Repo _repo;

    public GetLocalChangesTool(IGitStatusReader git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "get_local_changes";

    public string Description =>
        "The working tree's staged and unstaged file lists, each entry carrying a path and a "
        + "change status. Paths only — use get_diff for content.";

    public string JsonSchema => """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        return ReadTools.Project(
            _git.GetLocalChanges(_repo),
            (writer, value) =>
            {
                ReadTools.WriteSummary(writer, value.Summary);
                ReadTools.WriteFiles(writer, "staged", value.Staged);
                ReadTools.WriteFiles(writer, "unstaged", value.Unstaged);
            },
            "The working-tree read returned nothing.");
    }
}

internal sealed class GetDiffTool : IAssistantTool
{
    private readonly IGitService _git;
    private readonly Repo _repo;
    private readonly ISymbolExtractor _extractor;

    public GetDiffTool(IGitService git, Repo repo, ISymbolExtractor extractor)
    {
        _git = git;
        _repo = repo;
        _extractor = extractor;
    }

    public string Name => "get_diff";

    public string Description =>
        "The unified diff of one file. side picks what is compared: 'unstaged' (working tree vs "
        + "index), 'staged' (index vs HEAD), 'commit' (a commit vs its parent, needs commit_sha), "
        + "'range' (base_sha..commit_sha) or 'working_tree' (HEAD vs working tree, staged and "
        + "unstaged together). Lines come back prefixed '+', '-' or ' '. Repo-relative paths only: "
        + "ignored, credential-shaped and out-of-repository paths are refused.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative file path."},"side":{"type":"string","enum":["unstaged","staged","commit","range","working_tree"],"description":"Which comparison to diff."},"commit_sha":{"type":"string","description":"Commit to diff; required for side 'commit' and 'range' (the head side)."},"base_sha":{"type":"string","description":"Base commit for side 'range'."}},"required":["path","side"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var resolved = RepoFileGuard.ResolveForDiff(_git, _repo, ToolJson.String(args, "path"));
        if (resolved.Refusal is { } refusal)
            return Task.FromResult(ToolInvocation.Error(refusal));

        var sideName = ToolJson.String(args, "side");
        if (!TryParseSide(sideName, out var side))
            return Task.FromResult(ToolInvocation.Error(
                $"Argument 'side' must be one of unstaged, staged, commit, range, working_tree (got '{sideName}')."));

        var commitSha = ToolJson.String(args, "commit_sha");
        var baseSha = ToolJson.String(args, "base_sha");
        // A sha reaches git as a positional argument, so one starting with '-' would be read as a
        // diff option rather than a revision.
        if (LooksLikeOption(commitSha) || LooksLikeOption(baseSha))
            return Task.FromResult(ToolInvocation.Error("A commit sha may not begin with '-'."));
        if (side is DiffSide.Commit or DiffSide.Range && string.IsNullOrWhiteSpace(commitSha))
            return Task.FromResult(ToolInvocation.Error($"Argument 'commit_sha' is required for side '{sideName}'."));
        if (side == DiffSide.Range && string.IsNullOrWhiteSpace(baseSha))
            return Task.FromResult(ToolInvocation.Error("Argument 'base_sha' is required for side 'range'."));

        var diff = _git.GetDiff(_repo, resolved.RelativePath!, side, commitSha, baseSha);
        if (diff.ErrorMessage is { Length: > 0 } error)
            return Task.FromResult(ToolInvocation.Error(error));

        var annotations = DiffAnnotationCoordinator.ComputeOutlines(_extractor, _git, _repo, diff, commitSha, baseSha);
        var json = ToolJson.Write(writer => ReadTools.WriteDiffBody(writer, diff, annotations));
        return Task.FromResult(ToolInvocation.Ok(json));
    }

    private static bool LooksLikeOption(string? sha) => sha?.TrimStart().StartsWith('-') == true;

    private static bool TryParseSide(string? name, out DiffSide side)
    {
        switch (name)
        {
            case "unstaged": side = DiffSide.Unstaged; return true;
            case "staged": side = DiffSide.Staged; return true;
            case "commit": side = DiffSide.Commit; return true;
            case "range": side = DiffSide.Range; return true;
            case "working_tree": side = DiffSide.WorkingTree; return true;
            default: side = DiffSide.Unstaged; return false;
        }
    }
}

internal sealed class GetCommitHistoryTool : IAssistantTool
{
    private const int DefaultLimit = 40;
    private const int MaxLimit = 300;

    private readonly IGitHistoryReader _git;
    private readonly Repo _repo;

    public GetCommitHistoryTool(IGitHistoryReader git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "get_commit_history";

    public string Description =>
        "Recent commits, newest first: sha, summary line, author, timestamp, parents and any "
        + "branch or tag refs pointing at them. Use get_commit_details for a single commit's "
        + "full message and file list.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"limit":{"type":"integer","description":"How many commits to walk (1-300, default 40)."}},"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var limit = ToolJson.Int(args, "limit", DefaultLimit, 1, MaxLimit);
        return ReadTools.Project(
            _git.Load(_repo, limit),
            (writer, value) => Write(writer, value, limit),
            "The history read returned nothing.");
    }

    private static void Write(Utf8JsonWriter writer, CommitSnapshot snapshot, int limit)
    {
        if (snapshot.HeadBranchName is { Length: > 0 } head)
            writer.WriteString("head_branch", head);
        writer.WriteBoolean("truncated", snapshot.Truncated);

        writer.WritePropertyName("commits");
        writer.WriteStartArray();
        var count = 0;
        foreach (var commit in snapshot.Commits)
        {
            if (count++ >= limit)
                break;

            writer.WriteStartObject();
            writer.WriteString("sha", ReadTools.ShortSha(commit.Sha));
            writer.WriteString("summary", commit.Summary);
            writer.WriteString("author", commit.Author);
            ToolJson.WriteIso(writer, "when", commit.When);
            writer.WritePropertyName("parents");
            writer.WriteStartArray();
            foreach (var parent in commit.ParentShas)
                writer.WriteStringValue(ReadTools.ShortSha(parent));
            writer.WriteEndArray();
            if (commit.Refs.Count > 0)
            {
                writer.WritePropertyName("refs");
                writer.WriteStartArray();
                foreach (var badge in commit.Refs)
                    writer.WriteStringValue(badge.Name);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

internal sealed class GetCommitDetailsTool : IAssistantTool
{
    private readonly IGitHistoryReader _git;
    private readonly Repo _repo;

    public GetCommitDetailsTool(IGitHistoryReader git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "get_commit_details";

    public string Description =>
        "One commit's author, committer, full message, parents and the files it touched. "
        + "Accepts a full or abbreviated sha.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"sha":{"type":"string","description":"Commit sha, full or abbreviated."}},"required":["sha"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var sha = ToolJson.String(args, "sha");
        if (string.IsNullOrWhiteSpace(sha))
            return Task.FromResult(ToolInvocation.Error("Argument 'sha' is required."));

        return ReadTools.Project(_git.LoadDetails(_repo, sha), Write, "The commit read returned nothing.");
    }

    private static void Write(Utf8JsonWriter writer, CommitDetails details)
    {
        writer.WriteString("sha", ReadTools.ShortSha(details.Sha));
        writer.WriteString("author", $"{details.AuthorName} <{details.AuthorEmail}>");
        ToolJson.WriteIso(writer, "authored", details.AuthorWhen);
        writer.WriteString("committer", $"{details.CommitterName} <{details.CommitterEmail}>");
        ToolJson.WriteIso(writer, "committed", details.CommitterWhen);
        writer.WriteString("message", details.Message);
        writer.WritePropertyName("parents");
        writer.WriteStartArray();
        foreach (var parent in details.ParentShas)
            writer.WriteStringValue(ReadTools.ShortSha(parent));
        writer.WriteEndArray();
        ReadTools.WriteFiles(writer, "files", details.Files);
    }
}

internal sealed class GetBranchesTool : IAssistantTool
{
    private readonly IGitBranchOperations _git;
    private readonly Repo _repo;

    public GetBranchesTool(IGitBranchOperations git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "get_branches";

    public string Description =>
        "Local branches with their upstream tracking state, remote branches grouped by remote, "
        + "and the stash list.";

    public string JsonSchema => """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        return ReadTools.Project(_git.GetBranches(_repo), Write, "The branch read returned nothing.");
    }

    private static void Write(Utf8JsonWriter writer, BranchListing listing)
    {
        writer.WritePropertyName("local");
        writer.WriteStartArray();
        foreach (var branch in listing.LocalBranches)
        {
            writer.WriteStartObject();
            writer.WriteString("name", branch.Name);
            writer.WriteString("tip", ReadTools.ShortSha(branch.TipSha));
            switch (branch)
            {
                case LocalBranchEntry.Head head:
                    writer.WriteBoolean("current", true);
                    writer.WriteString("upstream", head.Upstream switch
                    {
                        HeadUpstreamState.Tracked => "tracked",
                        HeadUpstreamState.Gone => "gone",
                        _ => "none",
                    });
                    break;
                case LocalBranchEntry.Other other:
                    writer.WriteBoolean("current", false);
                    switch (other.Upstream)
                    {
                        case LocalUpstream.Tracked tracked:
                            writer.WriteString("upstream", $"{tracked.Remote}/{tracked.Branch}");
                            writer.WriteNumber("ahead", tracked.Sync.Ahead);
                            writer.WriteNumber("behind", tracked.Sync.Behind);
                            break;
                        case LocalUpstream.Gone:
                            writer.WriteString("upstream", "gone");
                            break;
                        default:
                            writer.WriteString("upstream", "none");
                            break;
                    }

                    break;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("remotes");
        writer.WriteStartArray();
        foreach (var remote in listing.Remotes)
        {
            writer.WriteStartObject();
            writer.WriteString("name", remote.Name);
            writer.WritePropertyName("branches");
            writer.WriteStartArray();
            foreach (var branch in remote.Branches)
                writer.WriteStringValue(branch.Name);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("stashes");
        writer.WriteStartArray();
        foreach (var stash in listing.Stashes)
        {
            writer.WriteStartObject();
            writer.WriteNumber("index", stash.Index);
            writer.WriteString("subject", stash.Subject);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
