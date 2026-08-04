using GitBench.Git;

namespace GitBench.Features.Diff.Reading;

/// <summary>
/// Keeps the abridged diff structurally honest: a block whose opening row survives keeps its
/// closing row too.
/// </summary>
/// <remarks>
/// Hiding a lone <c>}</c> saves the reader one row and costs them the shape of the file. The
/// method above appears never to end, the one below appears to be nested inside it, and the reader
/// is left reconstructing braces instead of reading the change — the exact opposite of what an
/// abridged diff is for.
///
/// So this is a compiler correction rather than a rejection: a plan that hides a dangling closer is
/// quietly given it back, instead of costing a minute-long run to redo. The model is never told,
/// because there is no decision here for it to make.
///
/// The matching is a text-level approximation — a brace inside a string literal or a comment counts
/// like any other. It errs toward keeping rows, which is the safe direction: the cost of being
/// wrong is one dull row on screen, not a misleading one.
/// </remarks>
internal static class ReadingDelimiters
{
    /// <summary>
    /// Reveals closing rows whose opener is still visible, shrinking any fold that swallowed them.
    /// </summary>
    public static void KeepDanglingClosers(
        ReadingRowIndex index,
        bool[] hidden,
        ReadingFoldRow?[] foldAt)
    {
        var ordinal = 0;
        for (var f = 0; f < index.Files.Count; f++)
        {
            if (!UsesBraces(LanguageRegistry.DetectLanguageId(index.Files[f].Path)))
            {
                foreach (var hunk in index.Files[f].Hunks) ordinal += hunk.Lines.Count;
                continue;
            }

            foreach (var hunk in index.Files[f].Hunks)
            {
                var start = ordinal;
                ordinal += hunk.Lines.Count;
                foreach (var side in (ReadOnlySpan<DiffLineKind>)[DiffLineKind.Added, DiffLineKind.Removed])
                    Balance(hunk, start, side, hidden, foldAt);
            }
        }
    }

    // Walks one side of a hunk carrying a stack of the rows that opened each still-open block. A
    // closer whose opener is visible must be visible too.
    private static void Balance(
        DiffHunk hunk,
        int start,
        DiffLineKind side,
        bool[] hidden,
        ReadingFoldRow?[] foldAt)
    {
        var openers = new Stack<int>();
        for (var l = 0; l < hunk.Lines.Count; l++)
        {
            var line = hunk.Lines[l];
            if (line.Kind != side && line.Kind != DiffLineKind.Context) continue;

            var row = start + l + 1;
            foreach (var c in line.Text)
            {
                if (c is '{' or '(' or '[')
                {
                    openers.Push(row);
                    continue;
                }
                if (c is not ('}' or ')' or ']')) continue;
                if (openers.Count == 0) continue;

                var opener = openers.Pop();
                if (hidden[opener - 1] || !hidden[row - 1]) continue;
                Reveal(row, hidden, foldAt);
            }
        }
    }

    // Un-hides one row. A row hidden by a removal simply reappears; a row hidden by a fold pulls the
    // fold back off it, and a fold left with nothing worth standing in for disappears entirely.
    private static void Reveal(int row, bool[] hidden, ReadingFoldRow?[] foldAt)
    {
        if (Covering(row, foldAt) is not { } fold)
        {
            hidden[row - 1] = false;
            return;
        }

        // Only a trailing row can be taken back without splitting the fold in two. A closer buried
        // inside a fold has its opener in there with it, so it never reaches here.
        if (row != fold.EndRow) return;

        foldAt[fold.StartRow - 1] = null;
        hidden[row - 1] = false;

        var span = fold.EndRow - fold.StartRow;
        if (span < 2)
        {
            for (var n = fold.StartRow; n < row; n++) hidden[n - 1] = false;
            return;
        }

        foldAt[fold.StartRow - 1] = fold with { EndRow = row - 1, HiddenCount = span };
    }

    private static ReadingFoldRow? Covering(int row, ReadingFoldRow?[] foldAt)
    {
        for (var back = row; back >= 1; back--)
            if (foldAt[back - 1] is { } fold)
                return fold.EndRow >= row ? fold : null;
        return null;
    }

    private static bool UsesBraces(string? languageId) => languageId switch
    {
        "csharp" or "go" or "java" or "kotlin" or "groovy" or "rust" or "swift" or "dart" or "scala"
            or "javascript" or "javascriptreact" or "typescript" or "typescriptreact"
            or "c" or "cpp" or "cuda-cpp" or "objective-c" or "objective-cpp" or "php" => true,
        _ => false,
    };
}
