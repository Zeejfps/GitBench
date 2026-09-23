using System.Text.Json;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Git;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>Where the pairing tools find a repository's live session. UI thread only.</summary>
internal interface IPairingSessions
{
    PairingStore? StoreFor(Guid repoId);
}

/// <summary>
/// The pairing loop's tools over one repository's live session: revise the roadmap, open a stop,
/// wait for the user, read where they are, end. Thin adapters over <see cref="PairingStore"/>,
/// which is UI-thread state — every call hops there first. None of them writes the user's code.
/// </summary>
internal static class PairingTools
{
    public static IReadOnlyList<IAssistantTool> CreateAll(Repo repo, IPairingSessions sessions, IUiDispatcher dispatcher)
    {
        var target = new PairingTarget(repo, sessions, dispatcher);
        return
        [
            new PairingRoadmapTool(target),
            new PairingStopTool(target),
            new PairingWaitTool(target),
            new PairingStateTool(target),
            new PairingWriteTestTool(target),
            new PairingHintTool(target),
            new PairingSayTool(target),
            new PairingShowTool(target),
            new PairingEndTool(target),
        ];
    }

    /// <summary>The wire shape of what the user did.</summary>
    internal static string WriteAction(PairingAction action, Func<string, string?> relative) => ToolJson.Write(writer =>
    {
        writer.WriteString("action", action switch
        {
            PairingAction.Done => "done",
            PairingAction.Message => "message",
            PairingAction.Skipped => "skipped",
            PairingAction.Hint => "hint",
            PairingAction.TestRan => "test_ran",
            PairingAction.TestUndone => "test_undone",
            PairingAction.Ended => "ended",
            PairingAction.Pending => "pending",
            PairingAction.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action."),
        });
        if (action.Stop > 0) writer.WriteNumber("stop", action.Stop);

        switch (action)
        {
            case PairingAction.Done done:
                writer.WriteString("diff", done.Diff.Length == 0 ? "(no changes)" : done.Diff);
                if (done.Test is { } test) WriteTest(writer, test);
                if (done.Forced) writer.WriteBoolean("closed_red", true);
                if (done.Problems.Count > 0)
                {
                    writer.WriteStartArray("problems");
                    foreach (var problem in done.Problems) writer.WriteStringValue(problem);
                    writer.WriteEndArray();
                }

                break;
            case PairingAction.Message message:
                writer.WriteString("text", message.Text);
                if (message.Caret is { } caret) WriteCaret(writer, caret, relative);
                break;
            case PairingAction.Skipped:
                break;
            case PairingAction.Hint hint:
                writer.WriteString("level", LevelName(hint.Level));
                writer.WriteString("meaning", hint.Level switch
                {
                    HintLevel.Location => "Light up the lines to change: pairing_hint level \"location\" with lines.",
                    HintLevel.Shape => "Show the signature or pseudocode: pairing_hint level \"shape\" with code. Not the implementation.",
                    HintLevel.Draft => "Show your code for this stop only: pairing_hint level \"draft\" with code.",
                    HintLevel.Intent => "No hint is needed.",
                    _ => throw new ArgumentOutOfRangeException(nameof(action), hint.Level, "Unknown level."),
                });
                break;
            case PairingAction.TestRan ran:
                WriteTest(writer, ran.Run);
                writer.WriteString("meaning", ran.Run switch
                {
                    TestRun.Failed => "The test fails, as it should: the stop is open for the user. Call pairing_wait.",
                    TestRun.Passed => "The test passed before any change, so it proves nothing, and it was taken back out. Write a test that fails, or change the stop.",
                    TestRun.Unrunnable => "The test could not be run. The user can fix the test command; you can write the test again.",
                    _ => throw new ArgumentOutOfRangeException(nameof(action), ran.Run, "Unknown run."),
                });
                break;
            case PairingAction.TestUndone:
                writer.WriteString("meaning", "The user took your test back out. Ask why in prose, or write a different one.");
                break;
            case PairingAction.Ended:
            case PairingAction.Pending:
            case PairingAction.Cancelled:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action.");
        }
    });

    internal static void WriteTest(Utf8JsonWriter writer, TestRun run)
    {
        writer.WritePropertyName("test");
        writer.WriteStartObject();
        switch (run)
        {
            case TestRun.Passed passed:
                writer.WriteString("outcome", "passed");
                writer.WriteString("output", passed.Output);
                break;
            case TestRun.Failed failed:
                writer.WriteString("outcome", "failed");
                writer.WriteNumber("exit_code", failed.ExitCode);
                writer.WriteString("output", failed.Output);
                break;
            case TestRun.Unrunnable unrunnable:
                writer.WriteString("outcome", "unrunnable");
                writer.WriteString("reason", unrunnable.Reason);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(run), run, "Unknown test run.");
        }

        writer.WriteEndObject();
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

    internal static string LevelName(HintLevel level) => level switch
    {
        HintLevel.Intent => "intent",
        HintLevel.Location => "location",
        HintLevel.Shape => "shape",
        HintLevel.Draft => "draft",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown hint level."),
    };
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
                        $"No pairing session is running for '{_repo.DisplayName}'. The user starts one in DiffDino "
                        + "with New pairing session.")));
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
        + "caret on the named declaration, and shows a card with the title and the reason. Returns "
        + "at once with where it landed (read line_text back and correct yourself if it is not the "
        + "place you meant); then call pairing_wait. Name the declaration, never a line number: "
        + "\"Class.Method\" or \"Method\". For a declaration that doesn't exist yet, name it in "
        + "symbol and pass the declaration it goes after in after. Fails while another stop is open, "
        + "unless replace is true. kind \"test\" is for a stop that starts with a test you write with "
        + "pairing_write_test; use \"edit\" otherwise. Explain why here, not the code to type.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path of the file."},"symbol":{"type":"string","description":"The declaration to put the user on, e.g. Client.Fetch."},"after":{"type":"string","description":"For a declaration that doesn't exist yet: the declaration it goes after."},"title":{"type":"string","description":"One line: what to do here."},"reason":{"type":"string","description":"Markdown: why this is the next place, and what the change has to achieve. No code."},"kind":{"type":"string","enum":["edit","test"],"description":"Default edit."},"replace":{"type":"boolean","description":"Take back the open stop and open this one instead. Default false."}},"required":["path","symbol","title","reason"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        foreach (var required in new[] { "path", "symbol", "title", "reason" })
            if (ToolJson.String(args, required) is not { Length: > 0 })
                return Task.FromResult(ToolInvocation.Error($"{required} must be a non-empty string."));

        var kind = ToolJson.String(args, "kind") switch
        {
            null or "edit" => PairingStopKind.Edit,
            "test" => PairingStopKind.Test,
            _ => (PairingStopKind?)null,
        };
        if (kind is not { } stopKind) return Task.FromResult(ToolInvocation.Error("kind must be \"edit\" or \"test\"."));

        var path = ToolJson.String(args, "path")!.Trim().Replace('\\', '/').TrimStart('/');
        var stopTarget = new StopTarget(path, ToolJson.String(args, "symbol")!.Trim(), ToolJson.String(args, "after")?.Trim());
        var title = ToolJson.String(args, "title")!.Trim();
        var reason = ToolJson.String(args, "reason")!.Trim();
        var replace = ToolJson.Bool(args, "replace", false);

        return target.OnStoreAsync(async store =>
        {
            switch (await store.OpenStopAsync(stopTarget, title, reason, stopKind, replace, ct))
            {
                case StopOpening.Opened opened:
                    return ToolInvocation.Ok(Describe(opened.Stop, stopTarget.Path, store.Hint.Value));
                case StopOpening.Refused refused:
                    return ToolInvocation.Error(refused.Message);
                default:
                    throw new InvalidOperationException("Unhandled stop opening.");
            }
        }, ct);
    }

    private static string Describe(OpenStop open, string path, HintLevel help) => ToolJson.Write(writer =>
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
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(open), open.Location, "Unknown location.");
        }

        writer.WriteString("hint_level", PairingTools.LevelName(help));
        writer.WriteString("next", help == HintLevel.Intent
            ? open.Stop.Kind == PairingStopKind.Test ? "Write the test with pairing_write_test, then call pairing_wait." : "Call pairing_wait."
            : $"The user wants help up to {PairingTools.LevelName(help)} at every stop: send pairing_hint for each level up to it, then call pairing_wait.");
    });
}

/// <summary>Waits for the user's next move.</summary>
internal sealed class PairingWaitTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_wait";

    public string Description =>
        $"Waits for the user, for at most {(int)PairingStore.WaitTimeout.TotalSeconds} seconds. Returns "
        + "{action:\"done\", stop, diff, test?} when they finish a stop — diff is exactly what they "
        + "changed since the stop was shown; read it, with what they told you in the conversation, "
        + "before deciding what's next. {action:\"message\", text, caret?} when they say something — a "
        + "question, or what they did instead of what you suggested: answer with pairing_say, keep it "
        + "in mind, then wait again. {action:\"skipped\"} when they pass on the stop. "
        + "{action:\"hint\", level} when they want more help at the open stop. {action:\"pending\"} "
        + "when the wait ran out: call pairing_wait again. {action:\"ended\"} when the user ended the "
        + "session: stop calling tools. {action:\"cancelled\"} when a newer wait took over.";

    public string JsonSchema => """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct) =>
        target.OnStoreAsync(async store =>
        {
            var action = await store.WaitAsync(ct);
            return ToolInvocation.Ok(PairingTools.WriteAction(action, store.Relative));
        }, ct);
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
                writer.WriteString("kind", open.Stop.Kind == PairingStopKind.Test ? "test" : "edit");
                writer.WriteString("hint_level", PairingTools.LevelName(store.Hint.Value));
                if (open.Test is { } test)
                {
                    writer.WriteString("test_path", test.Path);
                    writer.WriteString("test_name", test.Name);
                }

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

/// <summary>Writes the open test stop's test: the one write the agent gets, to test files only.</summary>
internal sealed class PairingWriteTestTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_write_test";

    public string Description =>
        "Writes one failing test for the open test stop (open it with pairing_stop kind \"test\" first). "
        + "The only file write you get, and only to test files: a file under a test directory, or one "
        + "named like FooTests.cs, foo.test.ts, foo_test.go or test_foo.py. content is the whole file. "
        + "Build and runner configuration files are refused. The user reads the test and runs it with "
        + "the repository's test command, filling {test} with test (one argument: no spaces, not "
        + "starting with '-'); the result arrives through pairing_wait as {action:\"test_ran\", "
        + "test:{outcome, output}}. It must fail: a test that passes before the user changes anything is "
        + "taken back out. Suggest the command in command the first time (for example \"dotnet test "
        + "--filter {test}\").";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path of the test file."},"content":{"type":"string","description":"The whole file."},"test":{"type":"string","description":"The test to run: a name or filter the test command takes, e.g. FullyQualifiedName~CalculatorTests.Multiply."},"command":{"type":"string","description":"The test command you suggest, with {test} where the test goes. Used only if the repository has none yet."}},"required":["path","content","test"],"additionalProperties":false}
        """;

    // A write: MCP clients must not read it as safe to call unasked.
    public bool IsWrite => true;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (ToolJson.String(args, "path") is not { Length: > 0 } path) return Task.FromResult(ToolInvocation.Error("path must be a non-empty string."));
        if (ToolJson.String(args, "content") is not { } content) return Task.FromResult(ToolInvocation.Error("content must be a string."));
        if (ToolJson.String(args, "test") is not { Length: > 0 } test) return Task.FromResult(ToolInvocation.Error("test must be a non-empty string."));
        var command = ToolJson.String(args, "command");
        var relative = path.Trim().Replace('\\', '/').TrimStart('/');

        return target.OnStoreAsync(async store =>
        {
            switch (await store.WriteTestAsync(relative, content, test.Trim(), command, ct))
            {
                case TestWriting.AwaitingUser:
                    return ToolInvocation.Ok(ToolJson.Write(writer =>
                    {
                        writer.WriteString("written", relative);
                        writer.WriteString("status", "awaiting_run");
                        writer.WriteString("next", "The user reads the test and runs it. Call pairing_wait for the result.");
                    }));
                case TestWriting.Refused refused:
                    return ToolInvocation.Error(refused.Message);
                default:
                    throw new InvalidOperationException("Unhandled test writing.");
            }
        }, ct);
    }
}

/// <summary>Answers a hint request at the open stop, at the level the user asked for.</summary>
internal sealed class PairingHintTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_hint";

    public string Description =>
        "Answers {action:\"hint\", level} from pairing_wait with help at the open stop, at that level "
        + "and never above it. location: lines, the line ranges to change in the stop's file, lit up "
        + "in the editor. shape: code, a signature or pseudocode, drawn as grey suggested lines at the "
        + "stop. draft: code, your code for this stop only, drawn as grey lines that disappear as the "
        + "user types them; the user can take it into the file. Then call pairing_wait.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"level":{"type":"string","enum":["location","shape","draft"],"description":"The level the user asked for."},"lines":{"type":"array","items":{"type":"object","properties":{"from":{"type":"integer","minimum":1,"description":"First line, 1-based."},"to":{"type":"integer","minimum":1,"description":"Last line, inclusive."}},"required":["from","to"],"additionalProperties":false},"description":"For location: the lines to change."},"code":{"type":"string","description":"For shape and draft: the code to show."}},"required":["level"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        PairingHint hint;
        switch (ToolJson.String(args, "level"))
        {
            case "location":
                var lines = new List<LineSpan>();
                if (args.TryGetProperty("lines", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object
                            || !item.TryGetProperty("from", out var from) || !from.TryGetInt32(out var first)
                            || !item.TryGetProperty("to", out var to) || !to.TryGetInt32(out var last)
                            || first < 1 || last < first)
                            return Task.FromResult(ToolInvocation.Error("Each of lines needs integers from and to, with 1 <= from <= to."));
                        lines.Add(new LineSpan(first, last));
                    }
                }

                if (lines.Count == 0) return Task.FromResult(ToolInvocation.Error("location needs lines."));
                hint = new PairingHint.Location(lines);
                break;
            case "shape":
                if (ToolJson.String(args, "code") is not { Length: > 0 } shape) return Task.FromResult(ToolInvocation.Error("shape needs code."));
                hint = new PairingHint.Shape(shape);
                break;
            case "draft":
                if (ToolJson.String(args, "code") is not { Length: > 0 } draft) return Task.FromResult(ToolInvocation.Error("draft needs code."));
                hint = new PairingHint.Draft(draft);
                break;
            default:
                return Task.FromResult(ToolInvocation.Error("level must be \"location\", \"shape\" or \"draft\"."));
        }

        return target.OnStoreAsync(store => store.ShowHint(hint) is { } refused
            ? ToolInvocation.Error(refused)
            : ToolInvocation.Ok(ToolJson.Write(writer =>
            {
                writer.WriteString("shown", PairingTools.LevelName(hint.Level));
                writer.WriteString("next", "Call pairing_wait.");
            })), ct);
    }
}

/// <summary>Says something to the user in the Pairing panel's conversation.</summary>
internal sealed class PairingSayTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_say";

    public string Description =>
        "Says something to the user in the Pairing panel's conversation: the answer to a message, or "
        + "a short remark about their diff. The user reads only what you send here — prose outside the "
        + "tools may never reach them. Markdown; keep it brief, and no code beyond the hint level they "
        + "asked for. Returns at once; then call pairing_wait.";

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
            return ToolInvocation.Ok(ToolJson.Write(writer => writer.WriteString("next", "Call pairing_wait.")));
        }, ct);
    }
}

/// <summary>Shows the user a place in the code while they talk, without opening a stop.</summary>
internal sealed class PairingShowTool(PairingTarget target) : IAssistantTool
{
    public string Name => "pairing_show";

    public string Description =>
        "Opens a file in DiffDino's editor for the user to look at, with the caret on a declaration "
        + "or a line: the test you wrote, a caller, where something is used — whatever they asked to "
        + "see. It is not a stop: the open stop stays open, and nothing is asked of the user. Name a "
        + "declaration in symbol, or pass line; with neither, the file opens at its top. Returns "
        + "where it landed; then answer with pairing_say or call pairing_wait.";

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
