using System.Text.Json;
using GitBench.Features.Commits;
using GitBench.Features.Review;
using GitBench.Git;

namespace GitBench.Features.Assistant.Tools;

/// <summary>
/// The review the assistant is looking at: the checked-out branch against the base a reviewer would
/// be given, and the files that range touches.
/// </summary>
/// <remarks>
/// Resolved per call rather than held, because a review is not a thing the app owns — the review
/// window resolves the same range the same way from <see cref="IGitService.ResolveAutoReviewBase"/>,
/// and a resolution cached here would drift from it after a rebase or a base pick.
/// </remarks>
internal sealed record ReviewScope(
    string HeadRef,
    string HeadSha,
    string BaseRef,
    string BaseSha,
    IReadOnlyList<ReviewIncrement> Increments,
    IReadOnlyList<FileChange> Files)
{
    private const int StackCap = 200;

    public FileChange? File(string path) =>
        Files.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.Ordinal));

    /// <summary>Resolves the range, or the sentence explaining why there is nothing to review.</summary>
    public static (ReviewScope? Scope, string? Error) Resolve(IGitService git, Repo repo, string? baseRef)
    {
        if (repo.Branch is not { Length: > 0 } headRef)
            return (null, "This repository has no checked-out branch (detached HEAD), so there is no review range.");

        ResolvedReviewBase resolved;
        if (!string.IsNullOrWhiteSpace(baseRef))
        {
            var sha = git.MergeBase(repo, baseRef, headRef) ?? baseRef;
            resolved = new ResolvedReviewBase(sha, baseRef, ReviewBaseKind.Explicit);
        }
        else if (git.ResolveAutoReviewBase(repo, headRef) is { } auto)
        {
            resolved = auto;
        }
        else
        {
            return (null, $"No review base resolves for '{headRef}': it has no upstream and no default "
                          + "branch to compare against. Pass base_ref to pick one.");
        }

        if (git.LoadReviewStack(repo, resolved.Sha, headRef, StackCap) is not Fetched<ReviewStack>.Ok stack)
            return (null, $"The review range for '{headRef}' could not be loaded.");

        if (git.LoadRangeFiles(repo, stack.Value.BaseSha, stack.Value.HeadSha) is not Fetched<IReadOnlyList<FileChange>>.Ok files)
            return (null, $"The files changed between {resolved.Ref} and {headRef} could not be listed.");

        return (new ReviewScope(
            headRef,
            stack.Value.HeadSha,
            resolved.Ref,
            stack.Value.BaseSha,
            stack.Value.Increments,
            files.Value), null);
    }

    public void WriteRange(Utf8JsonWriter writer)
    {
        writer.WriteString("head_ref", HeadRef);
        writer.WriteString("head_sha", ReadTools.ShortSha(HeadSha));
        writer.WriteString("base_ref", BaseRef);
        writer.WriteString("base_sha", ReadTools.ShortSha(BaseSha));
    }
}

/// The read-only surface over the review the user would open for the checked-out branch, plus the
/// one mark worth writing back.
internal static class ReviewTools
{
    public static IReadOnlyList<IAssistantTool> CreateReads(IGitService git, Repo repo) =>
    [
        new GetReviewStackTool(git, repo),
        new GetReviewDiffTool(git, repo),
        new GetFileAtBaseTool(git, repo),
    ];

    public static IReadOnlyList<IAssistantTool> CreateWrites(
        IGitService git,
        Repo repo,
        IReviewProgressStore progress,
        AssistantWriteSurface surface) =>
    [
        new MarkViewedTool(git, repo, progress, surface),
    ];
}

internal sealed class GetReviewStackTool : IAssistantTool
{
    private readonly IGitService _git;
    private readonly Repo _repo;

    public GetReviewStackTool(IGitService git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "get_review_stack";

    public string Description =>
        "The review for the checked-out branch: the base it is compared against, the commits on top "
        + "of it, and every file the range touches with its change status. Call this first when the "
        + "question is about a branch under review rather than the working tree.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"base_ref":{"type":"string","description":"Compare against this ref instead of the auto-resolved base."}},"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var (scope, error) = ReviewScope.Resolve(_git, _repo, ToolJson.String(args, "base_ref"));
        if (scope is null) return Task.FromResult(ToolInvocation.Error(error!));

        return Task.FromResult(ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            scope.WriteRange(writer);
            writer.WritePropertyName("commits");
            writer.WriteStartArray();
            foreach (var increment in scope.Increments)
            {
                writer.WriteStartObject();
                writer.WriteString("sha", increment.ShortSha);
                writer.WriteString("summary", increment.Summary);
                writer.WriteString("author", increment.Author);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            ReadTools.WriteFiles(writer, "files", scope.Files);
        })));
    }
}

internal sealed class GetReviewDiffTool : IAssistantTool
{
    private readonly IGitService _git;
    private readonly Repo _repo;

    public GetReviewDiffTool(IGitService git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "get_review_diff";

    public string Description =>
        "One file's diff across the whole review — base to branch tip, not the last commit and not "
        + "the working tree. This is what a reviewer reads; get_diff answers a different question.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path, as get_review_stack lists it."},"base_ref":{"type":"string","description":"Compare against this ref instead of the auto-resolved base."}},"required":["path"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var path = ToolJson.String(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ToolInvocation.Error("Argument 'path' is required."));

        var (scope, error) = ReviewScope.Resolve(_git, _repo, ToolJson.String(args, "base_ref"));
        if (scope is null) return Task.FromResult(ToolInvocation.Error(error!));

        var diff = _git.GetDiff(_repo, path, DiffSide.Range, scope.HeadSha, scope.BaseSha);
        if (diff.ErrorMessage is { Length: > 0 } message)
            return Task.FromResult(ToolInvocation.Error(message));

        return Task.FromResult(ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            scope.WriteRange(writer);
            ReadTools.WriteDiffBody(writer, diff);
        })));
    }
}

/// <summary>
/// Opens a range of one file as the review's base holds it, so a judgement about what a change
/// replaced rests on the old code rather than on a guess about it.
/// </summary>
/// <remarks>
/// Whether a path may be addressed at all is <see cref="RepoFileGuard"/>'s answer, the same one
/// <see cref="ReadFileTool"/> takes: reading at a ref is still reading this repository's files, and a
/// tool that skipped the guard would be the way around it. It asks through
/// <see cref="RepoFileGuard.ResolveForDiff"/> rather than <see cref="RepoFileGuard.Resolve"/>, since
/// a file the branch deletes is exactly the one worth reading at the base and is not on disk to open.
/// What is left here is how much comes back.
/// </remarks>
internal sealed class GetFileAtBaseTool : IAssistantTool
{
    private const int DefaultLines = 300;
    private const int MaxLines = 1200;

    // A ceiling on what one result may weigh, so a file of very long lines is capped by size even
    // when its line count is modest.
    private const int MaxBytes = 96 * 1024;

    private readonly IGitService _git;
    private readonly Repo _repo;

    public GetFileAtBaseTool(IGitService git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "get_file_at_base";

    public string Description =>
        "A file as it stood at the review's base, before the branch touched it — so a judgement "
        + "about what a change replaced rests on the old code rather than on a guess about it. "
        + "Repo-relative paths only: ignored, credential-shaped and out-of-repository paths are "
        + "refused.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path, as it is named at the base."},"start_line":{"type":"integer","description":"First line to return, 1-based. Default 1."},"line_count":{"type":"integer","description":"How many lines to return (1-1200, default 300)."},"base_ref":{"type":"string","description":"Compare against this ref instead of the auto-resolved base."}},"required":["path"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var resolved = RepoFileGuard.ResolveForDiff(_git, _repo, ToolJson.String(args, "path"));
        if (resolved.Refusal is { } refusal)
            return Task.FromResult(ToolInvocation.Error(refusal));

        var path = resolved.RelativePath!;
        var (scope, error) = ReviewScope.Resolve(_git, _repo, ToolJson.String(args, "base_ref"));
        if (scope is null) return Task.FromResult(ToolInvocation.Error(error!));

        var text = _git.GetFileText(_repo, path, DiffSide.Range, oldSide: true, scope.HeadSha, scope.BaseSha);
        if (text is null)
            return Task.FromResult(ToolInvocation.Error(
                $"'{path}' has no content at {scope.BaseRef} — the branch adds it, or the path differs there."));

        if (LooksBinary(text))
            return Task.FromResult(ToolInvocation.Error($"'{path}' is a binary file."));

        var start = ToolJson.Int(args, "start_line", 1, 1, int.MaxValue);
        var count = ToolJson.Int(args, "line_count", DefaultLines, 1, MaxLines);
        var all = text.Replace("\r\n", "\n").Split('\n');
        var from = Math.Min(start - 1, all.Length);

        var lines = new List<string>(Math.Min(count, 512));
        var bytes = 0;
        var cappedBySize = false;
        for (var i = from; i < all.Length && lines.Count < count; i++)
        {
            if (bytes + all[i].Length > MaxBytes)
            {
                cappedBySize = true;
                break;
            }

            lines.Add(all[i]);
            bytes += all[i].Length + 1;
        }

        return Task.FromResult(ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            scope.WriteRange(writer);
            writer.WriteString("path", path);
            writer.WriteNumber("start_line", start);
            writer.WriteNumber("end_line", from + lines.Count);
            writer.WriteNumber("total_lines", all.Length);
            writer.WriteBoolean("truncated", cappedBySize || from + lines.Count < all.Length);
            writer.WriteString("content", string.Join('\n', lines));
        })));
    }

    // A NUL in the first block is what git itself treats as "binary", and it keeps a compiled
    // artifact someone committed from arriving as several thousand replacement characters.
    private static bool LooksBinary(string text) =>
        text.AsSpan(0, Math.Min(text.Length, 8000)).IndexOf('\0') >= 0;
}

/// <summary>
/// Marks review files as read, through the same store a person's checkbox writes to.
/// </summary>
/// <remarks>
/// The mark carries the file's content identity, which is what makes it expire: a file that changes
/// after it was marked reads as unviewed again. Going around the store — writing a bare "seen" flag —
/// would produce a mark that never expires and quietly outranks the reviewer's own.
/// The store is the reviewer's own single-threaded state, so the marks are written where it lives
/// rather than on the turn's thread, which is a pool thread an open review window is drawing against.
/// </remarks>
internal sealed class MarkViewedTool : IAssistantTool
{
    private readonly IGitService _git;
    private readonly Repo _repo;
    private readonly IReviewProgressStore _progress;
    private readonly AssistantWriteSurface _surface;

    public MarkViewedTool(IGitService git, Repo repo, IReviewProgressStore progress, AssistantWriteSurface surface)
    {
        _git = git;
        _repo = repo;
        _progress = progress;
        _surface = surface;
    }

    public string Name => "mark_viewed";

    public string Description =>
        "Marks review files as Viewed, or clears the mark with viewed=false — the same checkbox the "
        + "reviewer ticks, and it expires the same way when the file changes again. Only mark what "
        + "you have actually read through.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"paths":{"type":"array","items":{"type":"string"},"description":"Repo-relative paths, as get_review_stack lists them."},"viewed":{"type":"boolean","description":"True to mark, false to clear. Default true."},"base_ref":{"type":"string","description":"Compare against this ref instead of the auto-resolved base."}},"required":["paths"],"additionalProperties":false}
        """;

    public bool IsWrite => true;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var paths = ToolJson.Strings(args, "paths");
        if (paths.Count == 0)
            return ToolInvocation.Error("Argument 'paths' must list at least one repo-relative path.");

        var viewed = args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("viewed", out var flag)
            && flag.ValueKind == JsonValueKind.False
                ? false
                : true;

        var (scope, error) = ReviewScope.Resolve(_git, _repo, ToolJson.String(args, "base_ref"));
        if (scope is null) return ToolInvocation.Error(error!);

        var files = new List<FileChange>(paths.Count);
        var unknown = new List<string>();
        foreach (var path in paths)
        {
            if (scope.File(path) is { } file)
                files.Add(file);
            else
                unknown.Add(path);
        }

        if (files.Count == 0)
            return ToolInvocation.Error(
                $"None of those paths are in the review of '{scope.HeadRef}': {string.Join(", ", unknown)}.");

        await _surface.OnUiThreadAsync(
            () =>
            {
                foreach (var file in files)
                    _progress.SetViewed(_repo.Id, scope.HeadRef, file.Path, file.ContentId, viewed);
            },
            ct).ConfigureAwait(false);

        var marked = files.Select(file => file.Path).ToArray();
        return ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("viewed", viewed);
            writer.WritePropertyName("paths");
            writer.WriteStartArray();
            foreach (var path in marked) writer.WriteStringValue(path);
            writer.WriteEndArray();
            if (unknown.Count == 0) return;
            writer.WritePropertyName("not_in_review");
            writer.WriteStartArray();
            foreach (var path in unknown) writer.WriteStringValue(path);
            writer.WriteEndArray();
        }));
    }
}
