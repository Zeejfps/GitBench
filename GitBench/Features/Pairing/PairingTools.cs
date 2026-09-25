using System.Text.Json;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.FileBrowser;
using GitBench.Git;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>Where the pairing tools find a repository's live session. UI thread only.</summary>
internal interface IPairingSessions
{
    PairingStore? StoreFor(Guid repoId);

    /// <summary>Opens a session the agent asked for, in the repository's conversation.</summary>
    AgentPairingStart StartByAgent(Guid repoId, string goal);
}

/// <summary>
/// The pairing loop's tools over one repository's live session: revise the roadmap, open a stop,
/// read where the user is, end. None of them waits on the user: their moves reach the agent as its
/// next turn. Thin adapters over <see cref="PairingStore"/>,
/// which is UI-thread state — every call hops there first. None of them writes the user's code.
/// </summary>
internal static class PairingTools
{
    public static IReadOnlyList<IAssistantTool> CreateAll(Repo repo, IPairingSessions sessions, IUiDispatcher dispatcher)
    {
        var target = new PairingTarget(repo, sessions, dispatcher);
        return
        [
            new PairingStartTool(target),
            new PairingRoadmapTool(target),
            new PairingStopTool(target),
            new PairingStateTool(target),
            new PairingSayTool(target),
            new PairingShowTool(target),
            new PairingEndTool(target),
        ];
    }

    internal static void WriteCaret(Utf8JsonWriter writer, EditorCaret caret, Func<string, string?> relative)
    {
        writer.WritePropertyName("caret");
        writer.WriteStartObject();
        writer.WriteString("path", relative(caret.Path) ?? caret.Path);
        writer.WriteNumber("line", caret.At.Line.Value);
        writer.WriteNumber("column", caret.At.Column.Value + 1);
        if (caret.SelectedText.Length > 0) writer.WriteString("selection", caret.SelectedText);
        writer.WriteEndObject();
    }
}

/// <summary>A repository's live pairing session, reached on the UI thread.</summary>
internal sealed class PairingTarget
{
    private readonly Repo _repo;
    private readonly IPairingSessions _sessions;
    private readonly IUiDispatcher _dispatcher;

    public PairingTarget(Repo repo, IPairingSessions sessions, IUiDispatcher dispatcher)
    {
        _repo = repo;
        _sessions = sessions;
        _dispatcher = dispatcher;
    }

    /// <summary>Runs <paramref name="work"/> against the session's store on the UI thread, or
    /// answers that there is no session. What it hands back is awaited off the UI thread.</summary>
    public async Task<ToolInvocation> OnStoreAsync(Func<PairingStore, Task<ToolInvocation>> work, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<Task<ToolInvocation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                completion.TrySetResult(_sessions.StoreFor(_repo.Id) is { } store
                    ? work(store)
                    : Task.FromResult(ToolInvocation.Error(
                        $"No pairing session is running for '{_repo.DisplayName}'. Call pairing_start once the user "
                        + "asks to pair, or they start one in DiffDino with New pairing session.")));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        var pending = await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        return await pending.ConfigureAwait(false);
    }

    public Task<ToolInvocation> OnStoreAsync(Func<PairingStore, ToolInvocation> work, CancellationToken ct) =>
        OnStoreAsync(store => Task.FromResult(work(store)), ct);

    /// <summary>Opens a session for the repository on the UI thread.</summary>
    public async Task<AgentPairingStart> StartAsync(string goal, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<AgentPairingStart>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                completion.TrySetResult(_sessions.StartByAgent(_repo.Id, goal));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public string RepoName => _repo.DisplayName;
}

/// <summary>Opens a pairing session in the conversation the user is having with the agent.</summary>
internal sealed class PairingStartTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_start";

    public string Description =>
        "Starts a pairing session on the repository, in the conversation the user is having with you in "
        + "DiffDino's panel: call it once the user asks to pair on a change, or agrees to your offer to. "
        + "The Pairing panel shows the goal; you navigate with the other pairing_* tools and the user "
        + "writes the code. Returns how the session runs; follow it, starting with pairing_roadmap.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"goal":{"type":"string","description":"The change to make, in a sentence or two, as the user would put it."}},"required":["goal"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (ToolJson.String(args, "goal") is not { } goal || string.IsNullOrWhiteSpace(goal))
            return ToolInvocation.Error("goal must be a non-empty string.");
        return await target.StartAsync(goal, ct).ConfigureAwait(false) switch
        {
            AgentPairingStart.Started => ToolInvocation.Ok(ToolJson.Write(writer => writer.WriteString("instructions", PairingInstructions.Started))),
            AgentPairingStart.AlreadyRunning running => ToolInvocation.Error(
                $"A pairing session is already running: \"{running.Store.Goal}\". Carry it on, or call pairing_end first."),
            AgentPairingStart.NoConversation => ToolInvocation.Error(
                $"No conversation with an agent is open for '{target.RepoName}' in DiffDino, so there is nowhere to "
                + "show the session. The user starts one there with New pairing session."),
            _ => throw new InvalidOperationException("Unhandled pairing start."),
        };
    }
}

/// <summary>Replaces the roadmap shown in the Pairing panel.</summary>
internal sealed class PairingRoadmapTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_roadmap";

    public string Description =>
        "Replaces the pairing roadmap: 3 to 7 coarse milestones toward the goal, no code. The panel "
        + "marks what this revision added and dropped. Send it at the start, and again whenever the "
        + "user's code departs from the plan. Mark milestones the user has finished as done.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"milestones":{"type":"array","minItems":1,"items":{"type":"object","properties":{"title":{"type":"string","description":"One line, no code."},"done":{"type":"boolean","description":"Finished by the user. Default false."}},"required":["title"],"additionalProperties":false},"description":"The milestones, in order."}},"required":["milestones"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var milestones = new List<Milestone>();
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("milestones", out var list)
            || list.ValueKind != JsonValueKind.Array)
            return Task.FromResult(ToolInvocation.Error("milestones must be an array."));

        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || ToolJson.String(item, "title") is not { Length: > 0 } title)
                return Task.FromResult(ToolInvocation.Error($"milestones[{index}].title must be a non-empty string."));
            milestones.Add(new Milestone(title.Trim(), ToolJson.Bool(item, "done", false)));
            index++;
        }

        if (milestones.Count == 0) return Task.FromResult(ToolInvocation.Error("Send at least one milestone."));
        return target.OnStoreAsync(store =>
        {
            store.SetRoadmap(milestones);
            return ToolInvocation.Ok(ToolJson.Write(writer => writer.WriteNumber("milestones", milestones.Count)));
        }, ct);
    }
}

/// <summary>Opens the next stop: the file, the declaration, and why this is the next place.</summary>
internal sealed class PairingStopTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_stop";

    public string Description =>
        "Takes the user to the next place to change: opens the file in DiffDino's editor with the "
        + "caret on the named declaration, shows a card with the title and the reason, and draws "
        + "your code for this stop into the file as grey suggested lines. The user accepts it with "
        + "one click or types it themselves; either way it comes back as done. code is ONE block for "
        + "this stop only. By default it replaces the whole declaration from the line with its name "
        + "to its last line (so it starts with the signature, without doc comments or attributes "
        + "above it); for a declaration that doesn't exist yet it goes in after the one named in "
        + "after; for a file that doesn't exist yet the file is created empty and code is its first "
        + "block only: the imports and the outline of its type, with members added at later stops. "
        + "For a small edit inside a "
        + "large declaration, pass lines (the lines code replaces) or after_line (the line code goes "
        + "in after) instead, as numbered in the file now. Lines at either end of code that read the "
        + "same as the file are trimmed off, so the lines the reply names can be narrower than what "
        + "you sent: that is expected, the stop is shown as it should be, and it needs no resending. "
        + "Returns at once; then end your turn: the user's move comes to you as your next message. "
        + "Send a stop again with replace: true only when the user asks for different code, or the "
        + "reply shows it landed in a different declaration than you meant. Name the declaration, "
        + "never a line number: \"Class.Method\" or \"Method\". Fails while another stop is open, "
        + "unless replace is true.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path of the file."},"symbol":{"type":"string","description":"The declaration to put the user on, e.g. Client.Fetch."},"after":{"type":"string","description":"For a declaration that doesn't exist yet: the declaration it goes after."},"title":{"type":"string","description":"One line: what to do here."},"reason":{"type":"string","description":"Markdown: why this is the next place, and what the change has to achieve. No code: the code goes in code."},"code":{"type":"string","description":"Your code for this stop: one block, exactly as it should read in the file, indented to fit."},"lines":{"type":"object","properties":{"from":{"type":"integer","minimum":1},"to":{"type":"integer","minimum":1}},"required":["from","to"],"additionalProperties":false,"description":"The lines code replaces, 1-based and inclusive, when not the whole declaration."},"after_line":{"type":"integer","minimum":1,"description":"The line code goes in after, when it replaces nothing."},"replace":{"type":"boolean","description":"Take back the open stop and open this one instead. Default false."}},"required":["path","symbol","title","reason","code"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        foreach (var required in new[] { "path", "symbol", "title", "reason" })
            if (ToolJson.String(args, required) is not { Length: > 0 })
                return Task.FromResult(ToolInvocation.Error($"{required} must be a non-empty string."));

        if (ToolJson.String(args, "code") is not { } code) return Task.FromResult(ToolInvocation.Error("code must be a string."));

        DraftSpan span;
        var hasLines = args.TryGetProperty("lines", out var linesArg);
        var hasAfter = args.TryGetProperty("after_line", out var afterArg);
        if (hasLines && hasAfter) return Task.FromResult(ToolInvocation.Error("Pass lines or after_line, not both."));
        if (hasLines)
        {
            if (linesArg.ValueKind != JsonValueKind.Object
                || !linesArg.TryGetProperty("from", out var fromArg) || !fromArg.TryGetInt32(out var from)
                || !linesArg.TryGetProperty("to", out var toArg) || !toArg.TryGetInt32(out var to))
                return Task.FromResult(ToolInvocation.Error("lines needs integers from and to."));
            span = new DraftSpan.Lines(from, to);
        }
        else if (hasAfter)
        {
            if (!afterArg.TryGetInt32(out var afterLine)) return Task.FromResult(ToolInvocation.Error("after_line must be an integer."));
            span = new DraftSpan.After(afterLine);
        }
        else
        {
            span = new DraftSpan.Declaration();
        }

        var path = ToolJson.String(args, "path")!.Trim().Replace('\\', '/').TrimStart('/');
        var stopTarget = new StopTarget(path, ToolJson.String(args, "symbol")!.Trim(), ToolJson.String(args, "after")?.Trim());
        var title = ToolJson.String(args, "title")!.Trim();
        var reason = ToolJson.String(args, "reason")!.Trim();
        var replace = ToolJson.Bool(args, "replace", false);

        return target.OnStoreAsync(async store =>
        {
            switch (await store.OpenStopAsync(stopTarget, title, reason, new DraftRequest(code, span), replace, ct))
            {
                case StopOpening.Opened opened:
                    return ToolInvocation.Ok(Describe(opened.Stop, stopTarget.Path));
                case StopOpening.Refused refused:
                    return ToolInvocation.Error(refused.Message);
                default:
                    throw new InvalidOperationException("Unhandled stop opening.");
            }
        }, ct);
    }

    private static string Describe(OpenStop open, string path) => ToolJson.Write(writer =>
    {
        writer.WriteNumber("stop", open.Stop.Number);
        writer.WriteString("path", path);
        switch (open.Location)
        {
            case StopLocation.OnSymbol symbol:
                writer.WriteString("placed", "on_symbol");
                writer.WriteNumber("line", symbol.At.Line.Value);
                writer.WriteString("line_text", symbol.LineText);
                break;
            case StopLocation.Insertion insertion:
                writer.WriteString("placed", "after");
                writer.WriteNumber("line", insertion.At.Line.Value);
                writer.WriteString("after", insertion.After);
                break;
            case StopLocation.NewFile:
                writer.WriteString("placed", "new_file");
                writer.WriteString("created", "The file was created empty and opened; your code is its first block.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(open), open.Location, "Unknown location.");
        }

        switch (open.Draft.Place)
        {
            case DraftPlace.Replace replace:
                writer.WriteString("code_replaces", $"lines {replace.Lines.From}-{replace.Lines.To}");
                break;
            case DraftPlace.InsertAfter insert:
                writer.WriteString("code_goes", $"after line {insert.Line.Value}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(open), open.Draft.Place, "Unknown draft place.");
        }

        writer.WriteString("next",
            "The stop is shown. Lines your code left as they were are trimmed, so the span above can be narrower than what "
            + "you sent; don't send it again. End your turn: the user's move comes to you as your next message.");
    });
}

/// <summary>Where the session and the user are.</summary>
internal sealed class PairingStateTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_state";

    public string Description =>
        "Reads the pairing session: the goal, the roadmap, the open stop, and where the user's caret "
        + "is with what they have selected.";

    public string JsonSchema => """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct) =>
        target.OnStoreAsync(store => ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteString("goal", store.Goal);
            writer.WriteString("phase", store.Phase.Value switch
            {
                PairingPhase.Starting => "starting",
                PairingPhase.Running => "running",
                PairingPhase.Disconnected => "disconnected",
                PairingPhase.Ended => "ended",
                PairingPhase.Failed => "failed",
                _ => throw new InvalidOperationException("Unknown phase."),
            });
            writer.WriteStartArray("roadmap");
            foreach (var entry in store.Roadmap.Value)
            {
                if (entry.Change == RoadmapChange.Removed) continue;
                writer.WriteStartObject();
                writer.WriteString("title", entry.Title);
                writer.WriteBoolean("done", entry.Done);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (store.Stop.Value is { } open)
            {
                writer.WritePropertyName("stop");
                writer.WriteStartObject();
                writer.WriteNumber("number", open.Stop.Number);
                writer.WriteString("title", open.Stop.Title);
                writer.WriteString("path", open.Stop.Target.Path);
                writer.WriteString("symbol", open.Stop.Target.Symbol);
                writer.WriteEndObject();
            }

            if (store.Caret.Value is { } caret) PairingTools.WriteCaret(writer, caret, store.Relative);
        })), ct);
}

/// <summary>Ends the session.</summary>
internal sealed class PairingEndTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_end";

    public string Description =>
        "Ends the pairing session once the roadmap is done, or when the user asks to stop. The summary "
        + "stays up in the Pairing panel.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"summary_md":{"type":"string","description":"Markdown: what was built, and anything left for later."}},"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var summary = ToolJson.String(args, "summary_md");
        return target.OnStoreAsync(store =>
        {
            store.End(summary);
            return ToolInvocation.Ok(ToolJson.Write(writer => writer.WriteBoolean("ok", true)));
        }, ct);
    }
}

/// <summary>Says something to the user in the Pairing panel's conversation.</summary>
internal sealed class PairingSayTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_say";

    public string Description =>
        "Says something to the user in the Pairing panel's conversation: the answer to a message, or "
        + "a short remark about their diff. The user reads only what you send here — prose outside the "
        + "tools may never reach them. Markdown; keep it brief: the stop's code goes in pairing_stop, "
        + "not here. Returns at once; end your turn once you have nothing more to do, and the user's answer comes to you as your next message.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"text_md":{"type":"string","description":"What to say, as markdown."}},"required":["text_md"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (ToolJson.String(args, "text_md") is not { Length: > 0 } text)
            return Task.FromResult(ToolInvocation.Error("text_md must be a non-empty string."));
        return target.OnStoreAsync(store =>
        {
            store.AddReply(text);
            return ToolInvocation.Ok(ToolJson.Write(writer => writer.WriteBoolean("ok", true)));
        }, ct);
    }
}

/// <summary>Shows the user a place in the code while they talk, without opening a stop.</summary>
internal sealed class PairingShowTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_show";

    public string Description =>
        "Opens a file in DiffDino's editor for the user to look at, with the caret on a declaration "
        + "or a line: a test, a caller, where something is used — whatever they asked to "
        + "see. It is not a stop: the open stop stays open, and nothing is asked of the user. Name a "
        + "declaration in symbol, or pass line; with neither, the file opens at its top. Returns "
        + "where it landed; then answer with pairing_say.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path of the file."},"symbol":{"type":"string","description":"The declaration to put the caret on, e.g. Client.Fetch."},"line":{"type":"integer","minimum":1,"description":"The 1-based line to put the caret on, when no symbol is named."}},"required":["path"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (ToolJson.String(args, "path") is not { Length: > 0 } rawPath)
            return Task.FromResult(ToolInvocation.Error("path must be a non-empty string."));
        var path = rawPath.Trim().Replace('\\', '/').TrimStart('/');
        var symbol = ToolJson.String(args, "symbol")?.Trim();
        int? line = args.TryGetProperty("line", out var lineArg) && lineArg.TryGetInt32(out var number) ? number : null;

        return target.OnStoreAsync(async store =>
        {
            switch (await store.ShowAsync(path, symbol, line, ct))
            {
                case Showing.Shown shown:
                    return ToolInvocation.Ok(ToolJson.Write(writer =>
                    {
                        writer.WriteString("path", path);
                        writer.WriteNumber("line", shown.Line);
                        if (shown.LineText is { } text) writer.WriteString("line_text", text);
                    }));
                case Showing.Refused refused:
                    return ToolInvocation.Error(refused.Message);
                default:
                    throw new InvalidOperationException("Unhandled showing.");
            }
        }, ct);
    }
}
