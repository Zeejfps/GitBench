using System.Text.Json;
using GitBench.Features.Diff;
using GitBench.Features.Review;
using GitBench.Features.Review.Walkthrough;
using GitBench.Git;
using ZGF.Observable;

namespace GitBench.Features.Assistant.Tools;

/// <summary>
/// How a narrator's step call returns: blocking on the reviewer, as an MCP session's does, or at
/// once, as the built-in assistant's does — its reviewer's Next and questions arrive as the next
/// user turn instead.
/// </summary>
internal enum WalkthroughNarratorMode
{
    Blocking,
    Immediate,
}

/// <summary>
/// The guided-walkthrough tools over one repository's open review window: show a batch of steps,
/// wait for the reviewer, end the walkthrough. Thin adapters over the window's
/// <see cref="ReviewWalkthroughStore"/>, which is UI-thread state — every call hops there first.
/// </summary>
internal static class WalkthroughTools
{
    public static IReadOnlyList<IAssistantTool> CreateAll(
        Repo repo, IReviewWindowRegistry windows, IUiDispatcher dispatcher, WalkthroughNarratorMode mode)
    {
        var target = new WalkthroughTarget(repo, windows, dispatcher);
        var narrator = mode switch
        {
            WalkthroughNarratorMode.Blocking => Narrator.TerminalAgent,
            WalkthroughNarratorMode.Immediate => Narrator.Assistant,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown narrator mode."),
        };
        return
        [
            new WalkthroughStepTool(target, narrator, mode),
            new WalkthroughWaitTool(target, mode),
            new WalkthroughEndTool(target),
        ];
    }

    /// <summary>The wire shape of what the reviewer did, shared by step and wait.</summary>
    internal static string WriteAction(WalkthroughAction action) => ToolJson.Write(writer =>
    {
        writer.WriteString("action", action switch
        {
            WalkthroughAction.Next => "next",
            WalkthroughAction.Ask => "ask",
            WalkthroughAction.Pending => "pending",
            WalkthroughAction.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action."),
        });
        writer.WriteNumber("at", WireIndex(action.At));
        if (action is not WalkthroughAction.Ask ask) return;
        writer.WriteString("question", ask.Question);
        if (ask.Selection is { } selection) WriteSelection(writer, selection);
    });

    /// <summary>Steps are numbered from 1 on the wire; 0 means no step was showing.</summary>
    internal static int WireIndex(int at) => at + 1;

    private static void WriteSelection(Utf8JsonWriter writer, DiffSelectionQuote selection)
    {
        writer.WritePropertyName("selection");
        writer.WriteStartObject();
        writer.WriteString("path", selection.Path);
        writer.WriteString("side", selection.Side switch
        {
            DiffQuoteSide.Added => "added",
            DiffQuoteSide.Removed => "removed",
            DiffQuoteSide.Context => "context",
            DiffQuoteSide.Mixed => "mixed",
            _ => throw new ArgumentOutOfRangeException(nameof(selection), selection.Side, "Unknown side."),
        });
        if (selection.StartLine is { } start) writer.WriteNumber("start_line", start.Value);
        if (selection.EndLine is { } end) writer.WriteNumber("end_line", end.Value);
        writer.WriteString("text", selection.Text);
        if (selection.Declaration is { Length: > 0 } declaration) writer.WriteString("declaration", declaration);
        writer.WriteEndObject();
    }
}

/// <summary>
/// The review window a repository's walkthrough tools drive — the most recently opened one for
/// that repository — reached on the UI thread, where the window registry and the store live.
/// </summary>
internal sealed class WalkthroughTarget
{
    private readonly Repo _repo;
    private readonly IReviewWindowRegistry _windows;
    private readonly IUiDispatcher _dispatcher;

    public WalkthroughTarget(Repo repo, IReviewWindowRegistry windows, IUiDispatcher dispatcher)
    {
        _repo = repo;
        _windows = windows;
        _dispatcher = dispatcher;
    }

    /// <summary>Runs <paramref name="work"/> against the window's store on the UI thread, or answers
    /// with the error that names the way back when no review window is open for the repository.</summary>
    public Task<ToolInvocation> OnStoreAsync(Func<ReviewWalkthroughStore, ToolInvocation> work, CancellationToken ct) =>
        OnUiThreadAsync(() => Find() is { } store ? work(store) : ToolInvocation.Error(NoWindow()), ct);

    /// <summary>Like <see cref="OnStoreAsync"/> for work that hands back something to wait on off
    /// the UI thread.</summary>
    public Task<Task<ToolInvocation>> OnStoreDeferredAsync(
        Func<ReviewWalkthroughStore, Task<ToolInvocation>> work, CancellationToken ct) =>
        OnUiThreadAsync(() => Find() is { } store ? work(store) : Task.FromResult(ToolInvocation.Error(NoWindow())), ct);

    private ReviewWalkthroughStore? Find() => _windows.LatestFor(_repo.Id)?.Walkthrough;

    private string NoWindow() =>
        $"No review window is open for '{_repo.DisplayName}'. Call review_open to open one, then send the steps again.";

    private Task<T> OnUiThreadAsync<T>(Func<T> work, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task.WaitAsync(ct);
    }
}

/// <summary>
/// Shows a batch of steps in the review window's walkthrough rail. Blocking narrators then wait on
/// the reviewer inside the same call; immediate ones return as soon as the batch is up.
/// </summary>
internal sealed class WalkthroughStepTool : IAssistantTool
{
    private readonly WalkthroughTarget _target;
    private readonly Narrator _narrator;
    private readonly WalkthroughNarratorMode _mode;

    public WalkthroughStepTool(WalkthroughTarget target, Narrator narrator, WalkthroughNarratorMode mode)
    {
        _target = target;
        _narrator = narrator;
        _mode = mode;
    }

    public string Name => "walkthrough_step";

    public string Description => _mode switch
    {
        WalkthroughNarratorMode.Blocking =>
            "Guides the reviewer through the change in the open review window: shows the first step "
            + "of the batch in the walkthrough rail — its title, its markdown body, the file and line "
            + "to focus, and the lines to spotlight with a numbered note each — and lets the reviewer "
            + "walk the rest of the batch with Next and Back. Returns when the reviewer steps past "
            + "the last step ({action:\"next\"}), asks a question ({action:\"ask\", question, "
            + "selection?} — answer it with more steps or with walkthrough_end), or after a bounded "
            + "wait ({action:\"pending\"} — call walkthrough_wait to keep waiting). 'at' is the step "
            + "the reviewer is on, numbered from 1 across every batch sent. Send two to four steps per "
            + "call; a later call appends to the same walkthrough. Lines are file lines on the new "
            + "side unless side is \"old\"; a line the diff does not hold shows as a notice in the rail.",
        WalkthroughNarratorMode.Immediate =>
            "Guides the reviewer through the change in the open review window: shows the first step "
            + "of the batch in the walkthrough rail — its title, its markdown body, the file and line "
            + "to focus, and the lines to spotlight with a numbered note each — and lets the reviewer "
            + "walk the rest of the batch with Next and Back. Returns {action:\"shown\", at} at once; "
            + "the reviewer's Next past the last step, or their question, arrives as your next "
            + "message. Anything you write outside this call shows under the current step. Send two "
            + "to four steps per call; a later call appends to the same walkthrough. Lines are file "
            + "lines on the new side unless side is \"old\"; a line the diff does not hold shows as a "
            + "notice in the rail.",
        _ => throw new ArgumentOutOfRangeException(nameof(_mode), _mode, "Unknown narrator mode."),
    };

    public string JsonSchema =>
        """
        {"type":"object","properties":{"steps":{"type":"array","minItems":1,"description":"The stops to show, in order.","items":{"type":"object","properties":{"title":{"type":"string","description":"One line: what this stop is about."},"body_md":{"type":"string","description":"Markdown: what the code does and how it connects to the rest."},"focus":{"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path, as get_review_stack lists it."},"line":{"type":"integer","minimum":1,"description":"1-based line number, as get_review_diff numbers it."},"side":{"type":"string","enum":["old","new"],"description":"Default new."}},"required":["path","line"],"additionalProperties":false,"description":"The file and line to scroll to."},"spotlights":{"type":"array","items":{"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path."},"side":{"type":"string","enum":["old","new"],"description":"Default new."},"from":{"type":"integer","minimum":1,"description":"First line, 1-based."},"to":{"type":"integer","minimum":1,"description":"Last line, inclusive."},"note":{"type":"string","description":"Listed beside the range's number in the rail."}},"required":["path","from","to"],"additionalProperties":false},"description":"Line ranges to light up, numbered in order."},"dim":{"type":"boolean","description":"Wash out the spotlit files' other lines. Default false."}},"required":["title","body_md"],"additionalProperties":false}}},"required":["steps"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        IReadOnlyList<WalkthroughStep> steps;
        switch (WalkthroughStepParser.Parse(args))
        {
            case WalkthroughStepParser.Result.Ok ok:
                steps = ok.Steps;
                break;
            case WalkthroughStepParser.Result.Invalid invalid:
                return ToolInvocation.Error(invalid.Message);
            default:
                throw new InvalidOperationException("Unknown parse result.");
        }

        switch (_mode)
        {
            case WalkthroughNarratorMode.Immediate:
                return await _target.OnStoreAsync(
                    store =>
                    {
                        store.Show(_narrator, steps);
                        return ToolInvocation.Ok(Shown(store.Current.Value));
                    },
                    ct).ConfigureAwait(false);

            case WalkthroughNarratorMode.Blocking:
            {
                var pending = await _target.OnStoreDeferredAsync(
                    store =>
                    {
                        store.Show(_narrator, steps);
                        return WaitOn(store, ct);
                    },
                    ct).ConfigureAwait(false);
                return await pending.ConfigureAwait(false);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(_mode), _mode, "Unknown narrator mode.");
        }
    }

    private static string Shown(int at) => ToolJson.Write(writer =>
    {
        writer.WriteString("action", "shown");
        writer.WriteNumber("at", WalkthroughTools.WireIndex(at));
    });

    private static async Task<ToolInvocation> WaitOn(ReviewWalkthroughStore store, CancellationToken ct) =>
        ToolInvocation.Ok(WalkthroughTools.WriteAction(await store.WaitAsync(ct).ConfigureAwait(false)));
}

/// <summary>Re-enters the wait on the reviewer after a <c>pending</c> return.</summary>
internal sealed class WalkthroughWaitTool : IAssistantTool
{
    private readonly WalkthroughTarget _target;
    private readonly WalkthroughNarratorMode _mode;

    public WalkthroughWaitTool(WalkthroughTarget target, WalkthroughNarratorMode mode)
    {
        _target = target;
        _mode = mode;
    }

    public string Name => "walkthrough_wait";

    public string Description => _mode switch
    {
        WalkthroughNarratorMode.Blocking =>
            "Waits for the reviewer after walkthrough_step returned {action:\"pending\"}: the same "
            + "return shape, for as long as it takes them to step past the last step or ask. Call it "
            + "again on every pending; call walkthrough_step to send more steps instead once they do.",
        WalkthroughNarratorMode.Immediate =>
            "Not for this narrator: the reviewer's Next and questions arrive as your next message, so "
            + "there is nothing to wait on. Reply to that message with walkthrough_step or "
            + "walkthrough_end.",
        _ => throw new ArgumentOutOfRangeException(nameof(_mode), _mode, "Unknown narrator mode."),
    };

    public string JsonSchema => """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsWrite => false;

    public async Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        switch (_mode)
        {
            case WalkthroughNarratorMode.Immediate:
                return ToolInvocation.Error(
                    "walkthrough_wait does not apply here: the reviewer's Next and questions arrive as "
                    + "your next message. Send steps with walkthrough_step, or finish with walkthrough_end.");

            case WalkthroughNarratorMode.Blocking:
            {
                var pending = await _target.OnStoreDeferredAsync(
                    store => store.Current.Value < 0
                        ? Task.FromResult(ToolInvocation.Error(
                            "No walkthrough is showing; send the first steps with walkthrough_step."))
                        : WaitOn(store, ct),
                    ct).ConfigureAwait(false);
                return await pending.ConfigureAwait(false);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(_mode), _mode, "Unknown narrator mode.");
        }
    }

    private static async Task<ToolInvocation> WaitOn(ReviewWalkthroughStore store, CancellationToken ct) =>
        ToolInvocation.Ok(WalkthroughTools.WriteAction(await store.WaitAsync(ct).ConfigureAwait(false)));
}

/// <summary>Ends the walkthrough: the steps and spotlights clear, and a summary, if given, stays
/// up as the closing card.</summary>
internal sealed class WalkthroughEndTool : IAssistantTool
{
    private readonly WalkthroughTarget _target;

    public WalkthroughEndTool(WalkthroughTarget target) => _target = target;

    public string Name => "walkthrough_end";

    public string Description =>
        "Ends the walkthrough: clears the steps and spotlights from the review window and, when "
        + "summary_md is given, leaves it up as a closing card. Call it once the reviewer has seen "
        + "the last stop, or when they ask to stop.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"summary_md":{"type":"string","description":"Markdown shown as the closing card; omit to just clear."}},"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("summary_md", out var given)
            && given.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            return Task.FromResult(ToolInvocation.Error("Argument 'summary_md' must be a string."));

        var summary = ToolJson.String(args, "summary_md");
        return _target.OnStoreAsync(
            store =>
            {
                store.End(summary);
                return ToolInvocation.Ok(ToolJson.Write(writer => writer.WriteBoolean("ok", true)));
            },
            ct);
    }
}

/// <summary>
/// Reads a step batch off the model's arguments, refusing anything the rail could not show
/// faithfully — a missing title, a line below 1, a range that runs backwards — with the path to
/// the offending field.
/// </summary>
internal static class WalkthroughStepParser
{
    internal abstract record Result
    {
        public sealed record Ok(IReadOnlyList<WalkthroughStep> Steps) : Result;

        public sealed record Invalid(string Message) : Result;
    }

    public static Result Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("steps", out var stepsElement)
            || stepsElement.ValueKind != JsonValueKind.Array)
            return new Result.Invalid("Argument 'steps' must be an array of at least one step.");

        if (stepsElement.GetArrayLength() == 0)
            return new Result.Invalid("Argument 'steps' must hold at least one step.");

        var steps = new List<WalkthroughStep>(stepsElement.GetArrayLength());
        var index = 0;
        foreach (var element in stepsElement.EnumerateArray())
        {
            var at = $"steps[{index}]";
            if (element.ValueKind != JsonValueKind.Object)
                return new Result.Invalid($"{at} must be an object.");

            var title = ToolJson.String(element, "title");
            if (string.IsNullOrWhiteSpace(title))
                return new Result.Invalid($"{at}.title is required.");

            var body = ToolJson.String(element, "body_md");
            if (body is null)
                return new Result.Invalid($"{at}.body_md is required.");

            ReviewLineRef? focus = null;
            if (element.TryGetProperty("focus", out var focusElement) && focusElement.ValueKind != JsonValueKind.Null)
            {
                if (focusElement.ValueKind != JsonValueKind.Object)
                    return new Result.Invalid($"{at}.focus must be an object.");
                var path = ToolJson.String(focusElement, "path");
                if (string.IsNullOrWhiteSpace(path))
                    return new Result.Invalid($"{at}.focus.path is required.");
                if (Line(focusElement, "line") is not { } line)
                    return new Result.Invalid($"{at}.focus.line must be a positive integer.");
                if (Side(focusElement, out var side) is { } sideError)
                    return new Result.Invalid($"{at}.focus.{sideError}");
                focus = new ReviewLineRef(path, side, line);
            }

            var spotlights = new List<ReviewSpotlight>();
            if (element.TryGetProperty("spotlights", out var spotlightsElement) && spotlightsElement.ValueKind != JsonValueKind.Null)
            {
                if (spotlightsElement.ValueKind != JsonValueKind.Array)
                    return new Result.Invalid($"{at}.spotlights must be an array.");
                var s = 0;
                foreach (var spot in spotlightsElement.EnumerateArray())
                {
                    var spotAt = $"{at}.spotlights[{s}]";
                    if (spot.ValueKind != JsonValueKind.Object)
                        return new Result.Invalid($"{spotAt} must be an object.");
                    var path = ToolJson.String(spot, "path");
                    if (string.IsNullOrWhiteSpace(path))
                        return new Result.Invalid($"{spotAt}.path is required.");
                    if (Line(spot, "from") is not { } from)
                        return new Result.Invalid($"{spotAt}.from must be a positive integer.");
                    if (Line(spot, "to") is not { } to)
                        return new Result.Invalid($"{spotAt}.to must be a positive integer.");
                    if (to < from)
                        return new Result.Invalid($"{spotAt}.to must not be below from.");
                    if (Side(spot, out var side) is { } sideError)
                        return new Result.Invalid($"{spotAt}.{sideError}");
                    var note = ToolJson.String(spot, "note");
                    spotlights.Add(new ReviewSpotlight(path, side, from, to, string.IsNullOrWhiteSpace(note) ? null : note));
                    s++;
                }
            }

            if (element.TryGetProperty("dim", out var dimElement)
                && dimElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                return new Result.Invalid($"{at}.dim must be a boolean.");
            var dim = ToolJson.Bool(element, "dim", false);

            steps.Add(new WalkthroughStep(title, body, focus, spotlights, dim));
            index++;
        }

        return new Result.Ok(steps);
    }

    // A file line: a positive integer, or nothing. Strings are refused — a line number the model
    // spelled as text is a sign it is guessing.
    private static FileLine? Line(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) return null;
        return number >= 1 ? new FileLine(number) : null;
    }

    // Which side the line is on; new when unsaid. Returns the message for a value that is neither.
    private static string? Side(JsonElement element, out DiffLineSide side)
    {
        side = DiffLineSide.New;
        if (!element.TryGetProperty("side", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        switch (value.ValueKind == JsonValueKind.String ? value.GetString() : null)
        {
            case "new":
                return null;
            case "old":
                side = DiffLineSide.Old;
                return null;
            default:
                return "side must be \"old\" or \"new\".";
        }
    }
}
