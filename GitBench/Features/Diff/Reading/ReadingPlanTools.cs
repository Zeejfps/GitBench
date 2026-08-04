using System.Text;
using System.Text.Json;
using GitBench.Features.Assistant.Tools;

namespace GitBench.Features.Diff.Reading;

/// <summary>
/// One abridgement in progress: the diffs being read, and the plan the model settled on.
/// </summary>
/// <remarks>
/// The tools write through this rather than returning the result up the loop, so the run reports
/// only whether <c>submit_plan</c> landed and the plan itself arrives here already compiled.
/// </remarks>
internal sealed class ReadingAbridgement
{
    public ReadingAbridgement(ReadingRowIndex index) => Index = index;

    public ReadingRowIndex Index { get; }

    /// <summary>The compiled result, or null until a plan is submitted.</summary>
    public ReadingOverlay? Result { get; private set; }

    /// <summary>The plan behind <see cref="Result"/>, kept as submitted so it is the plan — not a
    /// reconstruction of it — that goes into the cache.</summary>
    public ReadingPlan? Plan { get; private set; }

    public IReadOnlyList<IAssistantTool> Tools => [new PreviewPlanTool(this), new SubmitPlanTool(this)];

    internal void Accept(ReadingPlan plan, ReadingOverlay overlay)
    {
        Plan = plan;
        Result = overlay;
    }
}

/// <summary>The shared argument shape and feedback wording of both plan tools.</summary>
internal static class ReadingPlanToolProtocol
{
    /// <summary>Bumped whenever the edit protocol or a compiler invariant changes, so a cached plan
    /// built under the old rules is not reused under the new ones.</summary>
    public const string Version = "reading-plan-v1";

    public const string RangeItem =
        """{"type":"object","additionalProperties":false,"properties":{"start_row":{"type":"integer","minimum":1},"end_row":{"type":"integer","minimum":1}},"required":["start_row","end_row"]}""";

    public const string ElisionItem =
        """{"type":"object","additionalProperties":false,"properties":{"row":{"type":"integer","minimum":1},"old":{"type":"string","minLength":1},"new":{"type":"string"}},"required":["row","old","new"]}""";

    public static string Schema(bool withSummary)
    {
        var summary = withSummary
            ? ""","summary":{"type":"string","description":"One line describing what the change does."}"""
            : "";
        var required = withSummary
            ? "\"remove\",\"replace\",\"fold\",\"summary\""
            : "\"remove\",\"replace\",\"fold\"";
        return $$"""
            {"type":"object","additionalProperties":false,"properties":{"remove":{"type":"array","description":"Inclusive row ranges to hide. Coordinates always refer to the original numbering and never shift.","items":{{RangeItem}}},"replace":{"type":"array","description":"Within-line elisions. new must be old with spans deleted and each deletion shown as an ellipsis.","items":{{ElisionItem}}},"fold":{"type":"array","description":"Inclusive ranges of two or more contiguous same-marker rows in one hunk, replaced by one generated ellipsis row.","items":{{RangeItem}}}{{summary}}},"required":[{{required}}]}
            """;
    }

    public static ReadingPlan? Parse(JsonElement args, out string error)
    {
        error = string.Empty;
        if (args.ValueKind != JsonValueKind.Object)
        {
            error = "Arguments must be an object.";
            return null;
        }
        if (!TryRanges(args, "remove", out var remove, out error)) return null;
        if (!TryRanges(args, "fold", out var fold, out error)) return null;
        if (!TryElisions(args, out var replace, out error)) return null;

        return new ReadingPlan(
            remove.Select(r => new ReadingRemoval(r.Start, r.End)).ToArray(),
            replace,
            fold.Select(r => new ReadingFold(r.Start, r.End)).ToArray(),
            ToolJson.String(args, "summary"));
    }

    /// <summary>
    /// What the model is told about a draft: how much it kept, whether that is worth another look,
    /// and the diff its plan actually produces.
    /// </summary>
    public static string Feedback(ReadingOverlay overlay)
    {
        var stats = overlay.Stats;
        var b = new StringBuilder();
        b.Append("Valid plan. Retention: ")
            .Append(stats.VisibleChanged).Append('/').Append(stats.RawChanged)
            .Append(" changed rows (").Append(stats.RetainedPercent).Append("%); ")
            .Append(stats.RemovedChanged).Append(" hidden, ")
            .Append(stats.FoldedChanged).Append(" behind ").Append(stats.FoldCount).Append(" folds; files ")
            .Append(stats.VisibleFiles).Append('/').Append(stats.RawFiles).Append(".\n");

        b.Append(ReadingPreview.RetentionIsHigh(stats)
            ? "Retention is high. Take one more pass over repeated call sites, mechanical setup and "
              + "assertion batches that could become folds. This is advice, not a quota: keep every "
              + "distinct condition, transformation, effect and contract.\n"
            : "Retention is reasonable. Keep anything you are unsure about.\n");

        b.Append("Result (revised plans still use the ORIGINAL row numbers):\n");
        b.Append(ReadingPreview.Render(overlay));
        return b.ToString();
    }

    public static string Rejection(IReadOnlyList<string> problems) =>
        "The plan was not applied:\n- " + string.Join("\n- ", problems);

    private static bool TryRanges(
        JsonElement args,
        string name,
        out List<(int Start, int End)> ranges,
        out string error)
    {
        ranges = [];
        error = string.Empty;
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            error = $"'{name}' must be an array (use [] when there is nothing to add).";
            return false;
        }
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("start_row", out var start) || start.ValueKind != JsonValueKind.Number
                || !entry.TryGetProperty("end_row", out var end) || end.ValueKind != JsonValueKind.Number)
            {
                error = $"Every '{name}' entry needs integer start_row and end_row.";
                return false;
            }
            ranges.Add((start.GetInt32(), end.GetInt32()));
        }
        return true;
    }

    private static bool TryElisions(JsonElement args, out List<ReadingElision> elisions, out string error)
    {
        elisions = [];
        error = string.Empty;
        if (!args.TryGetProperty("replace", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            error = "'replace' must be an array (use [] when there is nothing to add).";
            return false;
        }
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("row", out var row) || row.ValueKind != JsonValueKind.Number
                || entry.TryGetProperty("old", out var old) != true || old.ValueKind != JsonValueKind.String
                || entry.TryGetProperty("new", out var replacement) != true
                || replacement.ValueKind != JsonValueKind.String)
            {
                error = "Every 'replace' entry needs an integer row and string old and new.";
                return false;
            }
            elisions.Add(new ReadingElision(row.GetInt32(), old.GetString()!, replacement.GetString()!));
        }
        return true;
    }
}

/// <summary>Compiles a draft and shows what it would produce, without ending the run.</summary>
internal sealed class PreviewPlanTool : IAssistantTool
{
    public const string ToolName = "preview_plan";

    private readonly ReadingAbridgement _abridgement;

    public PreviewPlanTool(ReadingAbridgement abridgement) => _abridgement = abridgement;

    public string Name => ToolName;

    public string Description =>
        "Check a complete remove/replace/fold plan against the numbered original diff and see the "
        + "reading diff it produces, with retention figures. Imports are hidden automatically and "
        + "never appear in a preview. Plans are always complete, never edits to an earlier draft.";

    public string JsonSchema => ReadingPlanToolProtocol.Schema(withSummary: false);

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (ReadingPlanToolProtocol.Parse(args, out var error) is not { } plan)
            return Task.FromResult(ToolInvocation.Error(error));

        var compiled = ReadingPlanCompiler.Compile(_abridgement.Index, plan);
        return Task.FromResult(compiled.Overlay is { } overlay
            ? ToolInvocation.Ok(ReadingPlanToolProtocol.Feedback(overlay))
            : ToolInvocation.Error(ReadingPlanToolProtocol.Rejection(compiled.Problems)));
    }
}

/// <summary>Accepts the final plan. A rejected submission is an error the model can answer.</summary>
internal sealed class SubmitPlanTool : IAssistantTool
{
    public const string ToolName = "submit_plan";

    private readonly ReadingAbridgement _abridgement;

    public SubmitPlanTool(ReadingAbridgement abridgement) => _abridgement = abridgement;

    public string Name => ToolName;

    public string Description =>
        "Submit the final remove/replace/fold plan against the numbered original diff, plus a "
        + "one-line summary of the change. DiffDino applies the plan itself; do not write out the "
        + "abridged diff.";

    public string JsonSchema => ReadingPlanToolProtocol.Schema(withSummary: true);

    // Nothing in the repository changes, so this never stops for approval: the plan only decides
    // what the reader is shown, and the raw diff is one keystroke away.
    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        if (_abridgement.Result != null)
            return Task.FromResult(ToolInvocation.Error("A plan has already been accepted."));
        if (ReadingPlanToolProtocol.Parse(args, out var error) is not { } plan)
            return Task.FromResult(ToolInvocation.Error(error));
        if (string.IsNullOrWhiteSpace(plan.Summary))
            return Task.FromResult(ToolInvocation.Error("'summary' is required: one line describing the change."));

        var compiled = ReadingPlanCompiler.Compile(_abridgement.Index, plan);
        if (compiled.Overlay is not { } overlay)
            return Task.FromResult(ToolInvocation.Error(ReadingPlanToolProtocol.Rejection(compiled.Problems)));

        _abridgement.Accept(plan, overlay);
        return Task.FromResult(ToolInvocation.Ok(ReadingPlanToolProtocol.Feedback(overlay)));
    }
}
