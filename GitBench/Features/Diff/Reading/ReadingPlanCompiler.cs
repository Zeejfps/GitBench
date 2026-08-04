using GitBench.Git;

namespace GitBench.Features.Diff.Reading;

/// <summary>A compiled plan, or the reasons it was rejected.</summary>
internal sealed record ReadingCompilation(ReadingOverlay? Overlay, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Overlay != null;
}

/// <summary>
/// Turns a <see cref="ReadingPlan"/> into a <see cref="ReadingOverlay"/>, rejecting anything that
/// would let the abridged diff say something the source did not.
/// </summary>
/// <remarks>
/// The plan carries coordinates and nothing else — every character the reader sees is either an
/// untouched source row, a generated ellipsis row whose marker and indent are derived here, or a
/// source row with spans deleted under <see cref="ReadingElisionRule"/>. That is what makes it
/// safe to put a model in this loop: it chooses what to hide, never what to say.
/// </remarks>
internal static class ReadingPlanCompiler
{
    private const int MaxProblems = 24;

    public static ReadingCompilation Compile(ReadingRowIndex index, ReadingPlan plan)
    {
        var problems = new List<string>();
        var count = index.Count;

        var mandatory = MandatoryImportMask(index);
        var hidden = (bool[])mandatory.Clone();
        var foldAt = new ReadingFoldRow?[count];
        var elided = new string?[count];

        var removedByPlan = new bool[count];
        ApplyRemovals(index, plan, hidden, removedByPlan, problems);
        var folded = ApplyFolds(index, plan, hidden, foldAt, mandatory, problems);
        ApplyElisions(index, plan, hidden, elided, mandatory, problems);

        if (problems.Count > 0)
            return new ReadingCompilation(null, problems);

        // Structure the plan did not mean to take: a closing row whose opener is still on screen
        // comes back, so the result reads as code rather than as a method that never ends.
        ReadingDelimiters.KeepDanglingClosers(index, hidden, foldAt);

        var stats = Measure(index, hidden, foldAt, removedByPlan, mandatory, out var fileEmpty);
        return new ReadingCompilation(
            new ReadingOverlay(index, hidden, foldAt, elided, fileEmpty, stats, plan.Summary),
            []);
    }

    /// <summary>The rows hidden whatever the plan says, so a caller can render a reading diff
    /// before any model has run.</summary>
    public static ReadingOverlay Mechanical(ReadingRowIndex index) =>
        Compile(index, ReadingPlan.Empty).Overlay!;

    private static bool[] MandatoryImportMask(ReadingRowIndex index)
    {
        var mask = new bool[index.Count];
        var ordinal = 0;
        foreach (var file in index.Files)
        {
            var language = LanguageRegistry.DetectLanguageId(file.Path);
            foreach (var hunk in file.Hunks)
            {
                var imports = ReadingImports.Classify(hunk.Lines, language);
                for (var l = 0; l < hunk.Lines.Count; l++)
                    mask[ordinal + l] = imports[l];
                ordinal += hunk.Lines.Count;
            }
        }
        return mask;
    }

    private static void ApplyRemovals(
        ReadingRowIndex index,
        ReadingPlan plan,
        bool[] hidden,
        bool[] removedByPlan,
        List<string> problems)
    {
        for (var i = 0; i < plan.Remove.Count; i++)
        {
            var r = plan.Remove[i];
            if (!InRange(index, r.StartRow, r.EndRow))
            {
                Report(problems, $"remove[{i}]: {r.StartRow}-{r.EndRow} is not a range inside rows 1-{index.Count}.");
                continue;
            }
            for (var n = r.StartRow; n <= r.EndRow; n++)
            {
                if (removedByPlan[n - 1])
                {
                    Report(problems, $"remove[{i}]: overlaps an earlier removal at row {n}.");
                    break;
                }
                removedByPlan[n - 1] = true;
                hidden[n - 1] = true;
            }
        }
    }

    private static List<ReadingFoldRow> ApplyFolds(
        ReadingRowIndex index,
        ReadingPlan plan,
        bool[] hidden,
        ReadingFoldRow?[] foldAt,
        bool[] mandatory,
        List<string> problems)
    {
        var folds = new List<ReadingFoldRow>();
        for (var i = 0; i < plan.Fold.Count; i++)
        {
            var f = plan.Fold[i];
            if (!InRange(index, f.StartRow, f.EndRow))
            {
                Report(problems, $"fold[{i}]: {f.StartRow}-{f.EndRow} is not a range inside rows 1-{index.Count}.");
                continue;
            }
            if (f.EndRow == f.StartRow)
            {
                Report(problems, $"fold[{i}]: a fold covers two or more rows; remove row {f.StartRow} instead.");
                continue;
            }
            if (!index.SameHunk(f.StartRow, f.EndRow))
            {
                Report(problems, $"fold[{i}]: rows {f.StartRow}-{f.EndRow} span more than one hunk.");
                continue;
            }

            var kind = index.Line(f.StartRow).Kind;
            var mixedMarker = false;
            var importRows = 0;
            for (var n = f.StartRow; n <= f.EndRow; n++)
            {
                if (index.Line(n).Kind != kind) mixedMarker = true;
                if (mandatory[n - 1]) importRows++;
            }
            if (mixedMarker)
            {
                Report(problems, $"fold[{i}]: rows {f.StartRow}-{f.EndRow} mix added, removed and context lines; fold one kind at a time.");
                continue;
            }

            var span = f.EndRow - f.StartRow + 1;
            if (importRows == span)
                continue;
            if (importRows > 0)
            {
                Report(problems, $"fold[{i}]: rows {f.StartRow}-{f.EndRow} cross import rows that are already hidden; fold only the rest.");
                continue;
            }

            var clash = 0;
            for (var n = f.StartRow; n <= f.EndRow; n++)
                if (hidden[n - 1]) { clash = n; break; }
            if (clash != 0)
            {
                Report(problems, $"fold[{i}]: row {clash} is already removed or folded.");
                continue;
            }

            var fold = new ReadingFoldRow(kind, CommonIndent(index, f.StartRow, f.EndRow), span, f.StartRow, f.EndRow);
            folds.Add(fold);
            foldAt[f.StartRow - 1] = fold;
            for (var n = f.StartRow; n <= f.EndRow; n++)
                hidden[n - 1] = true;
        }
        return folds;
    }

    private static void ApplyElisions(
        ReadingRowIndex index,
        ReadingPlan plan,
        bool[] hidden,
        string?[] elided,
        bool[] mandatory,
        List<string> problems)
    {
        for (var i = 0; i < plan.Replace.Count; i++)
        {
            var e = plan.Replace[i];
            if (!index.IsValidRow(e.Row))
            {
                Report(problems, $"replace[{i}]: row {e.Row} is outside rows 1-{index.Count}.");
                continue;
            }
            if (mandatory[e.Row - 1])
                continue;
            if (hidden[e.Row - 1])
            {
                Report(problems, $"replace[{i}]: row {e.Row} is already removed or folded.");
                continue;
            }
            if (!ReadingElisionRule.IsProjection(e.Old, e.New))
            {
                Report(problems, $"replace[{i}]: row {e.Row}'s replacement is not an elision of the original — characters may only be dropped, and every dropped span must show as an ellipsis.");
                continue;
            }
            var applied = ReadingElisionRule.Apply(index.Line(e.Row).Text, e.Old, e.New);
            if (applied is null)
            {
                Report(problems, $"replace[{i}]: row {e.Row} does not contain the quoted text exactly once.");
                continue;
            }
            elided[e.Row - 1] = applied;
        }
    }

    private static ReadingStats Measure(
        ReadingRowIndex index,
        bool[] hidden,
        ReadingFoldRow?[] foldAt,
        bool[] removedByPlan,
        bool[] mandatory,
        out bool[] fileEmpty)
    {
        fileEmpty = new bool[index.Files.Count];
        var rawChanged = 0;
        var visibleChanged = 0;
        var removedChanged = 0;
        var foldedChanged = 0;
        var visibleFiles = 0;

        var ordinal = 0;
        for (var f = 0; f < index.Files.Count; f++)
        {
            var fileChanged = 0;
            var fileVisible = 0;
            foreach (var hunk in index.Files[f].Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    ordinal++;
                    if (line.Kind == DiffLineKind.Context) continue;
                    rawChanged++;
                    fileChanged++;
                    if (!hidden[ordinal - 1])
                    {
                        visibleChanged++;
                        fileVisible++;
                        continue;
                    }
                    if (removedByPlan[ordinal - 1] || mandatory[ordinal - 1]) removedChanged++;
                    else foldedChanged++;
                }
            }
            fileEmpty[f] = fileChanged > 0 && fileVisible == 0;
            if (fileVisible > 0) visibleFiles++;
        }

        return new ReadingStats(
            rawChanged,
            visibleChanged,
            removedChanged,
            foldedChanged,
            FoldCount(foldAt),
            index.Files.Count,
            visibleFiles);
    }

    // Counted from the placed folds rather than the plan's, because the delimiter pass can shrink
    // a fold off a closing row or drop one that had nothing left to stand for.
    private static int FoldCount(ReadingFoldRow?[] foldAt)
    {
        var count = 0;
        foreach (var fold in foldAt)
            if (fold != null) count++;
        return count;
    }

    private static string CommonIndent(ReadingRowIndex index, int start, int end)
    {
        string? shortest = null;
        for (var n = start; n <= end; n++)
        {
            var text = index.Line(n).Text;
            if (text.Trim().Length == 0) continue;
            var indent = text[..LeadingWhitespace(text)];
            if (shortest is null || indent.Length < shortest.Length) shortest = indent;
        }
        return shortest ?? string.Empty;
    }

    private static int LeadingWhitespace(string text)
    {
        var i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return i;
    }

    private static bool InRange(ReadingRowIndex index, int start, int end) =>
        start >= 1 && end >= start && index.IsValidRow(start) && index.IsValidRow(end);

    private static void Report(List<string> problems, string message)
    {
        if (problems.Count < MaxProblems) problems.Add(message);
    }
}
