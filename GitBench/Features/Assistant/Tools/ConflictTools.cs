using System.Text;
using System.Text.Json;
using GitBench.Git;
using GitBench.Infrastructure;

namespace GitBench.Features.Assistant.Tools;

/// <summary>
/// The tools for a repository git has left half-merged: what is still conflicted, one path's three
/// sides in full, and the resolution that settles a path and stages it.
/// </summary>
/// <remarks>
/// A conflicted path is guarded like a diff rather than like a file being opened: one side of a
/// delete/modify has nothing in the working tree and a resolution may be about to create a file that
/// is not there yet, so the on-disk requirement <see cref="RepoFileGuard.Resolve"/> adds would refuse
/// exactly the conflicts worth settling. Everything else the guard decides still holds — a
/// conflicted <c>.env</c> is still a secret, and a conflict is a very good excuse to ask for one.
/// </remarks>
internal static class ConflictTools
{
    public static IReadOnlyList<IAssistantTool> CreateAll(IGitService git, Repo repo, AssistantWriteSurface surface) =>
    [
        new GetConflictsTool(git, repo),
        new GetConflictTool(git, repo),
        new ResolveConflictTool(git, repo, surface),
    ];

    /// <summary>One conflicted path the model may address, with the three sides already read.</summary>
    internal readonly record struct ConflictTarget(string RelativePath, string FullPath, ConflictStages Stages);

    internal static string OperationName(RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => "merge",
        RepoOperationState.Rebase => "rebase",
        RepoOperationState.CherryPick => "cherry_pick",
        RepoOperationState.Revert => "revert",
        RepoOperationState.Bisect => "bisect",
        RepoOperationState.ApplyMailbox => "apply_mailbox",
        RepoOperationState.UnmergedPaths => "unmerged_paths",
        _ => "none",
    };

    internal static string ChangeName(ConflictChangeKind kind) => kind switch
    {
        ConflictChangeKind.Added => "added",
        ConflictChangeKind.Deleted => "deleted",
        _ => "modified",
    };

    internal static ConflictChangeKind ChangeOf(string? side, bool hasBase) =>
        side is null ? ConflictChangeKind.Deleted
        : hasBase ? ConflictChangeKind.Modified
        : ConflictChangeKind.Added;

    /// <summary>Settles the path argument, or the sentence the model is told instead.</summary>
    internal static (ConflictTarget? Target, string? Refusal) Resolve(IGitService git, Repo repo, JsonElement args)
    {
        var resolved = RepoFileGuard.ResolveForDiff(git, repo, ToolJson.String(args, "path"));
        if (resolved.Refusal is { } refusal) return (null, refusal);

        var stages = git.GetConflictStages(repo, resolved.RelativePath!);
        if (stages is null)
            return (null,
                $"'{resolved.RelativePath}' is not a conflicted path in this repository. Call "
                + "get_conflicts for the paths that are.");

        return (new ConflictTarget(resolved.RelativePath!, resolved.FullPath!, stages), null);
    }
}

internal sealed class GetConflictsTool : IAssistantTool
{
    private readonly IGitService _git;
    private readonly Repo _repo;

    public GetConflictsTool(IGitService git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "get_conflicts";

    public string Description =>
        "Every path the in-progress merge, rebase, cherry-pick or revert left unmerged, and what "
        + "each side did to it — modified, added or deleted. An empty list means nothing is "
        + "conflicted, which is the answer, not a failure. Start here, then read one path with "
        + "get_conflict.";

    public string JsonSchema =>
        """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var conflicts = _git.GetConflictedPaths(_repo);
        var operation = ConflictTools.OperationName(_git.GetOperationState(_repo));

        return Task.FromResult(ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteString("operation", operation);
            writer.WritePropertyName("conflicts");
            writer.WriteStartArray();
            foreach (var conflict in conflicts)
            {
                writer.WriteStartObject();
                writer.WriteString("path", conflict.Path);
                writer.WriteString("ours", ConflictTools.ChangeName(conflict.Ours));
                writer.WriteString("theirs", ConflictTools.ChangeName(conflict.Theirs));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        })));
    }
}

/// <summary>
/// One conflicted path's three sides, as the index holds them rather than as the working tree's
/// conflict markers render them.
/// </summary>
/// <remarks>
/// Which operation is stuck comes back with the sides because it decides how to read them: a rebase
/// replays your commits onto theirs, so "ours" is the branch being rebased onto rather than the one
/// whose name is checked out. A side longer than <see cref="MaxSideLines"/> is cut short and says so
/// — one conflicted file must not spend the whole turn's budget, and a side cut short in silence is
/// one the model merges against believing it is whole.
/// </remarks>
internal sealed class GetConflictTool : IAssistantTool
{
    public const int MaxSideLines = 400;

    // A side of very long lines is capped by weight even when its line count is modest.
    private const int MaxSideBytes = 64 * 1024;

    private readonly IGitService _git;
    private readonly Repo _repo;

    public GetConflictTool(IGitService git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "get_conflict";

    public string Description =>
        "One conflicted path's three sides in full: 'base' as both started (absent when they both "
        + "added the file), 'ours' as the current side has it, 'theirs' as the incoming side does. "
        + "A side with no 'text' is one that deleted the file, which is not the same as empty. "
        + "'operation' says which operation is stuck — during a rebase 'ours' is the branch being "
        + "rebased onto. Sides longer than " + MaxSideLines + " lines come back truncated.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path of a conflicted file, as get_conflicts lists it."}},"required":["path"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var (target, refusal) = ConflictTools.Resolve(_git, _repo, args);
        if (refusal is not null) return Task.FromResult(ToolInvocation.Error(refusal));

        var found = target!.Value;
        var stages = found.Stages;
        // Decoded bytes are not text: a conflicted PNG handed back as several thousand replacement
        // characters costs the turn and tells the model nothing it can merge.
        if (LooksBinary(stages.Base) || LooksBinary(stages.Ours) || LooksBinary(stages.Theirs))
            return Task.FromResult(ToolInvocation.Error(
                $"'{found.RelativePath}' is a binary file, so its sides are not text to merge. "
                + "Settle it with resolve_conflict's 'ours' or 'theirs'."));

        var context = _git.GetConflictContext(_repo, found.RelativePath);
        var operation = ConflictTools.OperationName(context?.Operation ?? _git.GetOperationState(_repo));
        var hasBase = stages.Base is not null;

        return Task.FromResult(ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteString("path", found.RelativePath);
            writer.WriteString("operation", operation);
            if (stages.Base is { } baseText)
            {
                writer.WritePropertyName("base");
                writer.WriteStartObject();
                WriteText(writer, baseText);
                writer.WriteEndObject();
            }
            WriteSide(writer, "ours", context?.Ours.Label, stages.Ours, hasBase);
            WriteSide(writer, "theirs", context?.Theirs.Label, stages.Theirs, hasBase);
        })));
    }

    private static void WriteSide(Utf8JsonWriter writer, string name, string? label, string? text, bool hasBase)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        if (label is { Length: > 0 }) writer.WriteString("label", label);
        writer.WriteString("change", ConflictTools.ChangeName(ConflictTools.ChangeOf(text, hasBase)));
        if (text is not null) WriteText(writer, text);
        writer.WriteEndObject();
    }

    private static void WriteText(Utf8JsonWriter writer, string text)
    {
        var (body, truncated) = Clip(text);
        writer.WriteBoolean("truncated", truncated);
        writer.WriteString("text", body);
    }

    // Whole lines from the start, terminators kept, so what comes back is the side's own bytes as
    // far as it goes rather than a reflowed version of them.
    private static (string Text, bool Truncated) Clip(string text)
    {
        var kept = new StringBuilder(Math.Min(text.Length, MaxSideBytes));
        var lines = 0;
        foreach (var line in Lines(text))
        {
            if (lines == MaxSideLines || kept.Length + line.Length > MaxSideBytes)
                return (kept.ToString(), true);

            kept.Append(line);
            lines++;
        }

        return (kept.ToString(), false);
    }

    private static IEnumerable<string> Lines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            yield return text[start..end];
            start = end;
        }
    }

    // A NUL is what git itself treats as "binary", and the runner decodes a blob's bytes as UTF-8,
    // so one survives the decode even when the rest of the file did not.
    private static bool LooksBinary(string? text) => text is not null && text.IndexOf('\0') >= 0;
}

/// <summary>
/// Settles one conflicted path and stages it.
/// </summary>
/// <remarks>
/// 'both' is deliberately not offered. Concatenating the two sides is a starting point a person then
/// edits, and a usually-wrong final answer for a model that will not look at it again — the model
/// has both sides from <see cref="GetConflictTool"/> and can express a real merge as 'content'.
/// </remarks>
internal sealed class ResolveConflictTool : IAssistantTool
{
    private readonly IGitService _git;
    private readonly Repo _repo;
    private readonly AssistantWriteSurface _surface;

    public ResolveConflictTool(IGitService git, Repo repo, AssistantWriteSurface surface)
    {
        _git = git;
        _repo = repo;
        _surface = surface;
    }

    public string Name => "resolve_conflict";

    public string Description =>
        "Settles one conflicted path and stages it. 'ours' keeps the current side and 'theirs' the "
        + "incoming one — taking the side that deleted the file removes it rather than leaving an "
        + "empty one. 'content' writes the text you pass, byte for byte, which is how a merge that "
        + "is neither side lands: read both from get_conflict and write the merged result. Nothing "
        + "is committed and the other conflicted paths are left alone.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path of a conflicted file."},"resolution":{"type":"string","enum":["ours","theirs","content"],"description":"Which side to keep, or 'content' to write your own merged text."},"content":{"type":"string","description":"The resolved file in full. Required when resolution is 'content'; written verbatim, with no trailing newline added."}},"required":["path","resolution"],"additionalProperties":false}
        """;

    public bool IsWrite => true;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var resolution = ToolJson.String(args, "resolution")?.Trim();
        if (resolution is not ("ours" or "theirs" or "content"))
            return ToolInvocation.Error(resolution is { Length: > 0 }
                ? $"'{resolution}' is not a resolution this tool offers. Use 'ours', 'theirs', or "
                  + "'content' with the merged file — there is no 'both', because one side's text "
                  + "followed by the other's is almost never the file you meant."
                : "Argument 'resolution' is required: 'ours', 'theirs' or 'content'.");

        var content = ToolJson.String(args, "content");
        if (resolution == "content" && content is null)
            return ToolInvocation.Error(
                "Argument 'content' is required when resolution is 'content': it is the resolved "
                + "file in full, not a patch.");

        var (target, refusal) = ConflictTools.Resolve(_git, _repo, args);
        if (refusal is not null) return ToolInvocation.Error(refusal);

        var found = target!.Value;
        if (Apply(found, resolution, content) is GitOutcome.Failed failed)
            return ToolInvocation.Error(failed.Message);

        // Whether anything is left is the question the model asks next anyway, and answering it here
        // saves a round trip on every file of a multi-file conflict.
        var remaining = _git.GetConflictedPaths(_repo).Count;
        await _surface.NotifyAsync(MutationEffects.WorkingTree(_surface.Bus, _repo.Id), ct).ConfigureAwait(false);

        return ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteBoolean("ok", true);
            writer.WriteString("path", found.RelativePath);
            writer.WriteString("resolution", resolution);
            writer.WriteNumber("conflicts_remaining", remaining);
        }));
    }

    private GitOutcome Apply(ConflictTools.ConflictTarget target, string resolution, string? content)
    {
        if (resolution == "ours") return _git.TakeOurs(_repo, target.RelativePath);
        if (resolution == "theirs") return _git.TakeTheirs(_repo, target.RelativePath);

        try
        {
            var directory = Path.GetDirectoryName(target.FullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(target.FullPath, content!);
        }
        catch (Exception ex)
        {
            return new GitOutcome.Failed(ex.Message);
        }

        return _git.MarkResolved(_repo, target.RelativePath);
    }
}
