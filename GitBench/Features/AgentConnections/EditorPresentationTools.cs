using System.Globalization;
using System.Text.Json;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.Pairing;
using GitBench.Git;

namespace GitBench.Features.AgentConnections;

/// <summary>
/// The tools that point the user at code in one repository's Files pane — open a file on a
/// declaration or a line, light runs of lines up — whether or not a pairing session or a review is
/// running. They change the screen, never the repository, so none pauses for approval.
/// </summary>
internal static class EditorPresentationTools
{
    public static IReadOnlyList<IAssistantTool> CreateAll(
        Repo repo, IFileBrowserStore browsers, RepoFilePlaces places, AssistantWriteSurface surface)
    {
        var access = new EditorAccess(repo, browsers, places, surface);
        return
        [
            new EditorShowTool(access),
            new EditorSpotlightTool(access),
            new EditorClearTool(access),
        ];
    }
}

/// <summary>Reaches one repository's Files pane on the UI thread. Never switches repositories: with
/// another one on screen, the file waits in this repository's pane for the user to come back.</summary>
internal sealed record EditorAccess(Repo Repo, IFileBrowserStore Browsers, RepoFilePlaces Places, AssistantWriteSurface Surface)
{
    public string NoBrowser => $"'{Repo.DisplayName}' is not open in DiffDino, so it has no Files pane to show code in.";

    /// <summary>A repo-relative path argument as an absolute path inside the repository.</summary>
    public string? Absolute(string relative) => Places.Absolute(relative.Trim().Replace('\\', '/').TrimStart('/'));

    /// <summary>Opens the file with the caret at the start of a line, and runs <paramref name="then"/>
    /// against the pane. Answers whether the repository is the one on screen, or null when it has no
    /// pane at all.</summary>
    public Task<bool?> ShowAsync(string absolutePath, int line, Action<FileBrowserViewModel>? then, CancellationToken ct) =>
        Surface.OnUiThreadAsync<bool?>(() =>
        {
            if (Browsers.For(Repo.Id) is not { } browser) return null;
            browser.PlaceCaret(absolutePath, TextPosition.At(line, 0));
            then?.Invoke(browser);
            return ReferenceEquals(Browsers.Active.Value, browser);
        }, ct);

    public static void WriteOnScreen(Utf8JsonWriter writer, bool onScreen)
    {
        writer.WriteBoolean("on_screen", onScreen);
        if (!onScreen)
            writer.WriteString("note", "Another repository is on screen; the file waits in this one's Files pane until the user switches back.");
    }

    /// <summary>The lines of a run as the tool reports them, cut short past a screenful.</summary>
    public static string Excerpt(IReadOnlyList<string> lines, LineSpan span)
    {
        var last = Math.Min(span.To, span.From + ExcerptLines - 1);
        var text = string.Join('\n', Enumerable.Range(span.From, last - span.From + 1).Select(n => lines[n - 1]));
        return last < span.To ? text + $"\n… ({span.To - last} more lines)" : text;
    }

    private const int ExcerptLines = 30;

    /// <summary>A declaration's lines, found by name in the file as the user has it.</summary>
    public async Task<(LineSpan? Lines, string Error)> DeclarationAsync(string path, string symbol, CancellationToken ct)
    {
        switch (await Places.LocateAsync(new StopTarget(path, symbol, null), ct).ConfigureAwait(false))
        {
            case StopPlacement.Placed { Location: StopLocation.OnSymbol found }:
                return (new LineSpan(found.At.Line.Value, found.LastLine.Value), string.Empty);
            case StopPlacement.Placed { Location: StopLocation.NewFile }:
                return (null, $"{path} does not exist, or is empty.");
            case StopPlacement.Placed { Location: StopLocation.Insertion }:
                throw new InvalidOperationException("A declaration looked up by name has no insertion point.");
            case StopPlacement.Missed missed:
                return (null, StopMisses.Describe(missed.Miss));
            default:
                throw new InvalidOperationException("Unhandled placement.");
        }
    }
}

/// <summary>Opens a file for the user to look at, on a declaration or a line.</summary>
internal sealed class EditorShowTool(EditorAccess access) : IAssistantTool
{
    public string Name => "editor_show";

    public string Description =>
        "Opens a file in DiffDino's Files pane for the user to look at, with the caret on a "
        + "declaration or a line: where something happens, a caller, a test — whatever they asked "
        + "to see. Name a declaration in symbol, or pass line; with neither, the file opens at its "
        + "top. Works any time, in a pairing session or not; during one it only moves the editor and "
        + "the open stop stays open. Returns the line it landed on — check it is the one you meant. "
        + "To light up the lines themselves, use editor_spotlight instead.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path of the file."},"symbol":{"type":"string","description":"The declaration to put the caret on, e.g. Client.Fetch."},"line":{"type":"integer","minimum":1,"description":"The 1-based line to put the caret on, when no symbol is named."}},"required":["path"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (ToolJson.String(args, "path") is not { Length: > 0 } path)
            return ToolInvocation.Error("Argument 'path' is required.");
        if (access.Absolute(path) is not { } absolute)
            return ToolInvocation.Error($"{path} is outside the repository.");
        if (!ReviewWindowAccess.TryParseLine(args, "line", out var line, out var lineError))
            return ToolInvocation.Error(lineError);

        if (await access.Places.ReadLinesAsync(absolute, ct).ConfigureAwait(false) is not { } lines)
            return ToolInvocation.Error($"{path} is not a file in the repository, or is too large to open.");

        int at;
        if (ToolJson.String(args, "symbol")?.Trim() is { Length: > 0 } symbol)
        {
            var (found, error) = await access.DeclarationAsync(path, symbol, ct).ConfigureAwait(false);
            if (found is not { } span) return ToolInvocation.Error(error);
            at = span.From;
        }
        else
        {
            at = line?.Value ?? 1;
            if (at > lines.Count)
                return ToolInvocation.Error($"{path} has {lines.Count} lines; there is no line {at}.");
        }

        if (await access.ShowAsync(absolute, at, null, ct).ConfigureAwait(false) is not { } onScreen)
            return ToolInvocation.Error(access.NoBrowser);

        return ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteString("path", path);
            writer.WriteNumber("line", at);
            writer.WriteString("line_text", lines[Math.Min(at, lines.Count) - 1]);
            EditorAccess.WriteOnScreen(writer, onScreen);
        }));
    }
}

/// <summary>Opens a file and lights up runs of lines in it with numbered pins.</summary>
internal sealed class EditorSpotlightTool(EditorAccess access) : IAssistantTool
{
    public string Name => "editor_spotlight";

    public string Description =>
        "Opens a file in DiffDino's Files pane and lights up runs of lines in it with numbered pins, "
        + "the way the review window's spotlight does, scrolled to the first run. Use it to answer "
        + "\"where does X happen?\": light the lines that do it, with a short note on each, then "
        + "explain in your reply by pin number. Each run is a declaration by name (symbol) or a line "
        + "range (from, to). One file at a time: a later call replaces the set, editor_clear removes "
        + "it. The runs follow the lines as the user types. Returns the text of every run — check "
        + "it is what you meant.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path of the file."},"spotlights":{"type":"array","minItems":1,"items":{"type":"object","properties":{"symbol":{"type":"string","description":"A declaration to light whole, e.g. Client.Fetch. Instead of from/to."},"from":{"type":"integer","minimum":1,"description":"First line, 1-based."},"to":{"type":"integer","minimum":1,"description":"Last line, inclusive. Default: from."},"note":{"type":"string","description":"A few words on what this run is, drawn beside its pin."}},"additionalProperties":false},"description":"The runs to light, numbered in order."}},"required":["path","spotlights"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (ToolJson.String(args, "path") is not { Length: > 0 } path)
            return ToolInvocation.Error("Argument 'path' is required.");
        if (access.Absolute(path) is not { } absolute)
            return ToolInvocation.Error($"{path} is outside the repository.");
        if (!args.TryGetProperty("spotlights", out var entries) || entries.ValueKind != JsonValueKind.Array
            || entries.GetArrayLength() == 0)
            return ToolInvocation.Error("Argument 'spotlights' must be a non-empty array of {symbol} or {from, to?}, each with an optional note.");

        if (await access.Places.ReadLinesAsync(absolute, ct).ConfigureAwait(false) is not { } lines)
            return ToolInvocation.Error($"{path} is not a file in the repository, or is too large to open.");

        var runs = new List<EditorSpotlight>(entries.GetArrayLength());
        var index = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var (run, error) = await ParseAsync(entry, index, path, lines.Count, ct).ConfigureAwait(false);
            if (run is null) return ToolInvocation.Error(error);
            runs.Add(run);
            index++;
        }

        var spotlights = new EditorSpotlights(absolute, runs);
        if (await access.ShowAsync(absolute, runs[0].Lines.From, browser => browser.ShowSpotlights(spotlights), ct)
                .ConfigureAwait(false) is not { } onScreen)
            return ToolInvocation.Error(access.NoBrowser);

        return ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteString("path", path);
            writer.WritePropertyName("spotlights");
            writer.WriteStartArray();
            for (var i = 0; i < runs.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("pin", i + 1);
                writer.WriteNumber("from", runs[i].Lines.From);
                writer.WriteNumber("to", runs[i].Lines.To);
                writer.WriteString("text", EditorAccess.Excerpt(lines, runs[i].Lines));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            EditorAccess.WriteOnScreen(writer, onScreen);
        }));
    }

    // One entry parsed at the boundary: the run, or the precise complaint about it.
    private async Task<(EditorSpotlight? Run, string Error)> ParseAsync(
        JsonElement entry, int index, string path, int lineCount, CancellationToken ct)
    {
        var where = string.Create(CultureInfo.InvariantCulture, $"spotlights[{index}]");
        if (entry.ValueKind != JsonValueKind.Object)
            return (null, $"{where} must be an object {{symbol}} or {{from, to?}}, with an optional note.");

        var note = ToolJson.String(entry, "note")?.Trim() is { Length: > 0 } given ? given : null;
        if (ToolJson.String(entry, "symbol")?.Trim() is { Length: > 0 } symbol)
        {
            var (found, error) = await access.DeclarationAsync(path, symbol, ct).ConfigureAwait(false);
            return found is { } span ? (new EditorSpotlight(span, note), string.Empty) : (null, $"{where}: {error}");
        }

        if (!ReviewWindowAccess.TryParseLine(entry, "from", out var from, out var fromError))
            return (null, $"{where}: {fromError}");
        if (from is not { } first)
            return (null, $"{where}: name a symbol, or pass 'from'.");
        if (!ReviewWindowAccess.TryParseLine(entry, "to", out var to, out var toError))
            return (null, $"{where}: {toError}");
        var last = to ?? first;
        if (last < first)
            return (null, $"{where}: 'to' ({last.Value}) is before 'from' ({first.Value}).");
        if (last.Value > lineCount)
            return (null, $"{where}: {path} has {lineCount} lines; there is no line {last.Value}.");
        return (new EditorSpotlight(new LineSpan(first.Value, last.Value), note), string.Empty);
    }
}

internal sealed class EditorClearTool(EditorAccess access) : IAssistantTool
{
    public string Name => "editor_clear";

    public string Description => "Removes the lines editor_spotlight lit up. The file stays open where it is.";

    public string JsonSchema => """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsWrite => false;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var cleared = await access.Surface.OnUiThreadAsync(() =>
        {
            if (access.Browsers.For(access.Repo.Id) is not { } browser) return false;
            browser.ShowSpotlights(null);
            return true;
        }, ct).ConfigureAwait(false);
        return cleared ? ToolInvocation.Ok("""{"ok":true}""") : ToolInvocation.Error(access.NoBrowser);
    }
}
