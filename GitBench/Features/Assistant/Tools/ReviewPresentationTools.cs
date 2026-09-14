using System.Globalization;
using System.Text.Json;
using GitBench.Features.Diff;
using GitBench.Features.Review;
using GitBench.Features.Review.Walkthrough;
using GitBench.Git;
using GitBench.Messages;

namespace GitBench.Features.Assistant.Tools;

/// <summary>
/// The tools that drive one repository's review window — open it, point it at a file or line,
/// light lines up, read what the reviewer sees. They change the screen, never the repository, so
/// none pauses for approval.
/// </summary>
/// <remarks>
/// Every window model is UI-thread state, so each tool hops there through the write surface, the
/// same way <see cref="MarkViewedTool"/> reaches the progress store. A focus or spotlight then
/// waits, off that thread, for the window to lay the line out — the tool result quotes the text
/// it landed on, which is only known once it has.
/// </remarks>
internal static class ReviewPresentationTools
{
    public static IReadOnlyList<IAssistantTool> CreateAll(
        IGitService git, Repo repo, IReviewWindowRegistry windows, AssistantWriteSurface surface)
    {
        var access = new ReviewWindowAccess(repo, windows, surface);
        return
        [
            new ReviewStateTool(access),
            new ReviewOpenTool(git, access),
            new ReviewFocusTool(access),
            new ReviewSpotlightTool(access),
            new ReviewClearTool(access),
        ];
    }
}

/// <summary>Reaches this repository's review window on the UI thread. Several may be open for one
/// repository (one per head); the most recently opened is the one a narrator means.</summary>
internal sealed record ReviewWindowAccess(Repo Repo, IReviewWindowRegistry Windows, AssistantWriteSurface Surface)
{
    public const string NoWindow =
        "No review window is open for this repository. Call review_open first.";

    /// <summary>UI thread only.</summary>
    public ReviewWindowViewModel? Latest() => Windows.LatestFor(Repo.Id);

    /// <summary>Runs <paramref name="work"/> against the window on the UI thread; null when there
    /// is no window to run it against.</summary>
    public Task<T?> OnWindowAsync<T>(Func<ReviewWindowViewModel, T> work, CancellationToken ct) where T : class =>
        Surface.OnUiThreadAsync(() => Latest() is { } window ? work(window) : null, ct);

    public static string SideName(DiffLineSide side) => side == DiffLineSide.Old ? "old" : "new";

    /// <summary>Parses a side argument at the boundary: "old", "new" or absent (new).</summary>
    public static bool TryParseSide(JsonElement args, out DiffLineSide side, out string error)
    {
        side = DiffLineSide.New;
        error = string.Empty;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("side", out var value)
            || value.ValueKind == JsonValueKind.Null)
            return true;
        switch (value.ValueKind == JsonValueKind.String ? value.GetString() : null)
        {
            case "new": return true;
            case "old": side = DiffLineSide.Old; return true;
            default:
                error = $"Argument 'side' must be \"old\" or \"new\" (got {value.GetRawText()}).";
                return false;
        }
    }

    /// <summary>Parses a 1-based line number argument: a positive integer, or absent.</summary>
    public static bool TryParseLine(JsonElement args, string name, out FileLine? line, out string error)
    {
        line = null;
        error = string.Empty;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 1)
        {
            line = new FileLine(number);
            return true;
        }
        error = $"Argument '{name}' must be a line number of 1 or more (got {value.GetRawText()}).";
        return false;
    }

    public static void WriteQuote(Utf8JsonWriter writer, string name, DiffSelectionQuote quote)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("path", quote.Path);
        if (quote.StartLine is { } start) writer.WriteNumber("from", start.Value);
        if (quote.EndLine is { } end) writer.WriteNumber("to", end.Value);
        writer.WriteString("kind", quote.Side.ToString().ToLowerInvariant());
        if (quote.Declaration is { Length: > 0 } declaration) writer.WriteString("declaration", declaration);
        writer.WriteString("text", quote.Text);
        writer.WriteEndObject();
    }

    /// <summary>A focus result as the tool reports it: the line it landed on, or why it could not.</summary>
    public static ToolInvocation Report(ReviewLineResolution resolution) => resolution switch
    {
        ReviewLineResolution.Resolved resolved => ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteString("path", resolved.Line.Path);
            writer.WriteString("side", SideName(resolved.Line.Side));
            writer.WriteNumber("line", resolved.Line.Line.Value);
            writer.WriteString("text", resolved.Text);
        })),
        ReviewLineResolution.NotInDiff missing => ToolInvocation.Error(Describe(missing)),
        ReviewLineResolution.NoSuchFile none => ToolInvocation.Error(Describe(none)),
        ReviewLineResolution.Unavailable unavailable => ToolInvocation.Error(Describe(unavailable)),
        _ => throw new InvalidOperationException($"Unhandled resolution {resolution.GetType().Name}."),
    };

    /// <summary>One spotlight's result inside the set's array: resolved with its text, or the
    /// reason it is not on screen — the set still applied, so this is a status, not an error.</summary>
    public static void WriteSpotlightResult(Utf8JsonWriter writer, int index, ReviewSpotlight spotlight, ReviewLineResolution resolution)
    {
        writer.WriteStartObject();
        writer.WriteNumber("pin", index + 1);
        writer.WriteString("path", spotlight.Path);
        writer.WriteString("side", SideName(spotlight.Side));
        writer.WriteNumber("from", spotlight.From.Value);
        writer.WriteNumber("to", spotlight.To.Value);
        switch (resolution)
        {
            case ReviewLineResolution.Resolved resolved:
                writer.WriteString("status", "resolved");
                writer.WriteString("text", resolved.Text);
                break;
            case ReviewLineResolution.NotInDiff missing:
                writer.WriteString("status", "not_in_diff");
                writer.WriteString("problem", Describe(missing));
                break;
            case ReviewLineResolution.NoSuchFile none:
                writer.WriteString("status", "no_such_file");
                writer.WriteString("problem", Describe(none));
                break;
            case ReviewLineResolution.Unavailable unavailable:
                writer.WriteString("status", "unavailable");
                writer.WriteString("problem", Describe(unavailable));
                break;
            default:
                throw new InvalidOperationException($"Unhandled resolution {resolution.GetType().Name}.");
        }
        writer.WriteEndObject();
    }

    private static string Describe(ReviewLineResolution.NotInDiff missing)
    {
        var line = missing.Line;
        var neighbours = (missing.NearestBefore, missing.NearestAfter) switch
        {
            ({ } before, { } after) => $"the nearest lines it does hold are {before.Value} and {after.Value}",
            ({ } before, null) => $"the last line it holds before it is {before.Value}",
            (null, { } after) => $"the first line it holds after it is {after.Value}",
            _ => "it holds no lines on that side",
        };
        return $"The diff of '{line.Path}' has no line {line.Line.Value} on the {SideName(line.Side)} side; {neighbours}.";
    }

    private static string Describe(ReviewLineResolution.NoSuchFile none) =>
        $"'{none.Path}' is not a file of this review. get_review_stack lists the files the range touches.";

    private static string Describe(ReviewLineResolution.Unavailable unavailable) =>
        $"The diff of '{unavailable.Path}' cannot be shown: {unavailable.Reason}.";
}

internal sealed class ReviewStateTool : IAssistantTool
{
    private readonly ReviewWindowAccess _access;

    public ReviewStateTool(ReviewWindowAccess access) => _access = access;

    public string Name => "review_state";

    public string Description =>
        "What this repository's open review windows show: the range under review, the active "
        + "file, the lines on screen, the files marked Viewed, and the reviewer's current text "
        + "selection as a quote. Read this before pointing at anything, and to answer \"what's "
        + "this?\" about what the reviewer selected.";

    public string JsonSchema => """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsWrite => false;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var json = await _access.Surface.OnUiThreadAsync(Write, ct).ConfigureAwait(false);
        return ToolInvocation.Ok(json);
    }

    private string Write() => ToolJson.Write(writer =>
    {
        writer.WriteString("active_repo", _access.Repo.DisplayName);
        writer.WritePropertyName("windows");
        writer.WriteStartArray();
        foreach (var window in _access.Windows.Windows)
        {
            if (window.Session.RepoId != _access.Repo.Id) continue;
            writer.WriteStartObject();
            writer.WriteString("repo", _access.Repo.DisplayName);
            writer.WriteString("head", window.Session.HeadRef);
            writer.WriteString("base", window.BaseChipLabel.Value);
            if (window.ActiveFile.Value is { } active) writer.WriteString("active_file", active);
            if (window.Visible.Value is { } visible)
            {
                writer.WritePropertyName("visible");
                writer.WriteStartObject();
                writer.WriteString("path", visible.Path);
                writer.WriteNumber("from", visible.From.Value);
                writer.WriteNumber("to", visible.To.Value);
                writer.WriteEndObject();
            }
            writer.WritePropertyName("viewed");
            writer.WriteStartArray();
            foreach (var path in window.ViewedPaths()) writer.WriteStringValue(path);
            writer.WriteEndArray();
            if (window.Selection.Value is { } quote) ReviewWindowAccess.WriteQuote(writer, "selection", quote);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
}

/// <summary>Opens the review window for a range, or brings the open one forward.</summary>
/// <remarks>
/// The base is resolved here, the way the window itself would, so the result names the ref the
/// window will show rather than "auto"; an unresolvable range is refused before any window opens.
/// The registry answers the open request on the bus synchronously, so the window is in the
/// registry by the time the same UI-thread hop looks for it.
/// </remarks>
internal sealed class ReviewOpenTool : IAssistantTool
{
    private readonly IGitService _git;
    private readonly ReviewWindowAccess _access;

    public ReviewOpenTool(IGitService git, ReviewWindowAccess access)
    {
        _git = git;
        _access = access;
    }

    public string Name => "review_open";

    public string Description =>
        "Opens the review window for a branch, or focuses the one already open for it. head "
        + "defaults to the checked-out branch; base to the branch's upstream or the default "
        + "branch, the same base get_review_stack resolves.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"head":{"type":"string","description":"The branch to review. Default: the checked-out branch."},"base":{"type":"string","description":"Compare against this ref instead of the auto-resolved base."}},"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var repo = _access.Repo;
        var headArg = ToolJson.String(args, "head");
        string head;
        if (!string.IsNullOrWhiteSpace(headArg))
            head = headArg;
        else if (RepoHead.Branch(_git, repo) is { } checkedOut)
            head = checkedOut;
        else
            return ToolInvocation.Error(
                "This repository has no checked-out branch (detached HEAD); pass head to name the branch to review.");

        var baseArg = ToolJson.String(args, "base");
        string baseRef;
        string baseSha;
        if (!string.IsNullOrWhiteSpace(baseArg))
        {
            if (_git.MergeBase(repo, baseArg, head) is not { } mergeBase)
                return ToolInvocation.Error($"'{baseArg}' and '{head}' have no common ancestor, or one of them is not a ref.");
            baseRef = baseArg;
            baseSha = mergeBase;
        }
        else if (_git.ResolveAutoReviewBase(repo, head) is { } auto)
        {
            baseRef = auto.Ref;
            baseSha = auto.Sha;
        }
        else
        {
            return ToolInvocation.Error(
                $"No review base resolves for '{head}': it has no upstream and no default branch to "
                + "compare against. Pass base to pick one.");
        }

        if (_git.LoadReviewStack(repo, baseSha, head, 1) is Fetched<ReviewStack>.Failed failed)
            return ToolInvocation.Error($"The review range {baseRef}..{head} could not be loaded: {failed.Message}");

        var window = await _access.Surface.OnUiThreadAsync(
            () =>
            {
                _access.Surface.Bus.Broadcast(new OpenReviewWindowMessage(repo.Id, head, head, baseRef, baseRef));
                foreach (var open in _access.Windows.Windows)
                    if (open.Session.RepoId == repo.Id && open.Session.HeadRef == head)
                        return open;
                return null;
            },
            ct).ConfigureAwait(false);

        if (window is null)
            return ToolInvocation.Error($"The review window for '{head}' did not open.");

        return ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteString("repo", repo.DisplayName);
            writer.WriteString("head", head);
            writer.WriteString("base", baseRef);
        }));
    }
}

internal sealed class ReviewFocusTool : IAssistantTool
{
    private readonly ReviewWindowAccess _access;

    public ReviewFocusTool(ReviewWindowAccess access) => _access = access;

    public string Name => "review_focus";

    public string Description =>
        "Scrolls the review window to a file, or to one line of it (about a third of the way down "
        + "the viewport), loading the diff and opening a collapsed gap if it has to. Returns the "
        + "text of the line it landed on — check it against what you meant; a line the diff does "
        + "not hold is an error naming the nearest ones it does.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path, as get_review_stack lists it."},"line":{"type":"integer","description":"1-based line number, as get_review_diff numbers it. Omit to show the file's header."},"side":{"type":"string","enum":["old","new"],"description":"Which side numbers the line. Default new; use old for a removed line."}},"required":["path"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var path = ToolJson.String(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return ToolInvocation.Error("Argument 'path' is required.");
        if (!ReviewWindowAccess.TryParseSide(args, out var side, out var sideError))
            return ToolInvocation.Error(sideError);
        if (!ReviewWindowAccess.TryParseLine(args, "line", out var line, out var lineError))
            return ToolInvocation.Error(lineError);

        var pending = await _access.OnWindowAsync(
            window => line is { } at
                ? window.FocusLineAsync(new ReviewLineRef(path, side, at), ct)
                : window.FocusFileAsync(path, ct),
            ct).ConfigureAwait(false);
        if (pending is null) return ToolInvocation.Error(ReviewWindowAccess.NoWindow);

        return ReviewWindowAccess.Report(await pending.ConfigureAwait(false));
    }
}

internal sealed class ReviewSpotlightTool : IAssistantTool
{
    private readonly ReviewWindowAccess _access;

    public ReviewSpotlightTool(ReviewWindowAccess access) => _access = access;

    public string Name => "review_spotlight";

    public string Description =>
        "Lights up line ranges in the review window with numbered pins, replacing any earlier set. "
        + "dim washes out the rest of each spotlit file so the pinned lines stand out. Returns the "
        + "text of every range as it resolved — a range the diff does not hold reports why instead "
        + "of a pin on the wrong line. The reviewer's own text selection is never touched.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"spotlights":{"type":"array","items":{"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path."},"side":{"type":"string","enum":["old","new"],"description":"Which side numbers the range. Default new."},"from":{"type":"integer","description":"First line, 1-based."},"to":{"type":"integer","description":"Last line, inclusive. Default: from."},"note":{"type":"string","description":"What this range is, shown beside its pin number."}},"required":["path","from"],"additionalProperties":false},"description":"The ranges to light, numbered in order."},"dim":{"type":"boolean","description":"Wash out the rest of each spotlit file. Default false."}},"required":["spotlights"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("spotlights", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return ToolInvocation.Error("Argument 'spotlights' must be an array of {path, side?, from, to?, note?}.");

        var spotlights = new List<ReviewSpotlight>(entries.GetArrayLength());
        var index = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var (spotlight, error) = ParseSpotlight(entry, index);
            if (spotlight is null) return ToolInvocation.Error(error);
            spotlights.Add(spotlight);
            index++;
        }
        var dim = ToolJson.Bool(args, "dim", false);

        var pending = await _access.OnWindowAsync(
            window => window.SetSpotlightsAsync(spotlights, dim, ct), ct).ConfigureAwait(false);
        if (pending is null) return ToolInvocation.Error(ReviewWindowAccess.NoWindow);

        var resolutions = await pending.ConfigureAwait(false);
        return ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteBoolean("dim", dim);
            writer.WritePropertyName("spotlights");
            writer.WriteStartArray();
            for (var i = 0; i < spotlights.Count; i++)
                ReviewWindowAccess.WriteSpotlightResult(writer, i, spotlights[i], resolutions[i]);
            writer.WriteEndArray();
        }));
    }

    // One entry parsed at the boundary: the spotlight, or the precise complaint about it.
    private static (ReviewSpotlight? Spotlight, string Error) ParseSpotlight(JsonElement entry, int index)
    {
        var where = string.Create(CultureInfo.InvariantCulture, $"spotlights[{index}]");
        if (entry.ValueKind != JsonValueKind.Object)
            return (null, $"{where} must be an object {{path, side?, from, to?, note?}}.");

        var path = ToolJson.String(entry, "path");
        if (string.IsNullOrWhiteSpace(path))
            return (null, $"{where}: 'path' is required.");
        if (!ReviewWindowAccess.TryParseSide(entry, out var side, out var sideError))
            return (null, $"{where}: {sideError}");
        if (!ReviewWindowAccess.TryParseLine(entry, "from", out var from, out var fromError))
            return (null, $"{where}: {fromError}");
        if (from is not { } first)
            return (null, $"{where}: 'from' is required.");
        if (!ReviewWindowAccess.TryParseLine(entry, "to", out var to, out var toError))
            return (null, $"{where}: {toError}");
        var last = to ?? first;
        if (last < first)
            return (null, $"{where}: 'to' ({last.Value}) is before 'from' ({first.Value}).");

        var note = ToolJson.String(entry, "note");
        return (new ReviewSpotlight(path, side, first, last, string.IsNullOrWhiteSpace(note) ? null : note), string.Empty);
    }
}

internal sealed class ReviewClearTool : IAssistantTool
{
    private readonly ReviewWindowAccess _access;

    public ReviewClearTool(ReviewWindowAccess access) => _access = access;

    public string Name => "review_clear";

    public string Description => "Removes every spotlight from the review window. The viewport stays where it is.";

    public string JsonSchema => """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsWrite => false;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var cleared = await _access.OnWindowAsync(
            window =>
            {
                window.ClearSpotlights();
                return window;
            },
            ct).ConfigureAwait(false);
        if (cleared is null) return ToolInvocation.Error(ReviewWindowAccess.NoWindow);
        return ToolInvocation.Ok("""{"ok":true}""");
    }
}
