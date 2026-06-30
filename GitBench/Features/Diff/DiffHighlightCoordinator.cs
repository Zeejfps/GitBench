using GitBench.Git;
using GitBench.Theming;

namespace GitBench.Features.Diff;

/// <summary>
/// Orchestrates syntax highlighting for one diff: detect the language, fetch the needed file
/// blob(s) via <see cref="IGitService"/>, tokenize each whole file, and package the result as a
/// <see cref="DiffHighlight"/>. Pure orchestration with no threading of its own — the caller
/// (<see cref="DiffViewModel"/>) runs it on a background, generation-guarded lane so navigating
/// away discards stale results. Returns null whenever the diff should render plain (feature off,
/// unsupported language, binary/empty diff, or every side failed/was over-cap).
/// </summary>
internal static class DiffHighlightCoordinator
{
    // Shared across all diff view models: the engine is internally locked (thread-safe) and
    // caches loaded grammars, so one instance keeps the grammar cache warm app-wide.
    private static readonly SyntaxHighlighter Highlighter = new();

    public static DiffHighlight? Compute(IGitService git, Repo repo, DiffResult diff, string? commitSha, string? baseSha = null)
    {
        if (!DiffOptions.SyntaxHighlightingEnabled) return null;
        if (diff.IsBinary || diff.ErrorMessage != null || diff.Hunks.Count == 0) return null;

        var languageId = LanguageRegistry.DetectLanguageId(diff.Path);
        if (languageId == null) return null;

        // Only fetch the side(s) the diff actually shows: a pure-add diff has no removed rows
        // (skip the old blob), a pure-delete no added/context rows (skip the new blob).
        var (needOld, needNew) = NeededSides(diff);

        var oldSpans = needOld ? HighlightSide(git, repo, diff, commitSha, baseSha, oldSide: true, languageId) : null;
        var newSpans = needNew ? HighlightSide(git, repo, diff, commitSha, baseSha, oldSide: false, languageId) : null;
        if (oldSpans == null && newSpans == null) return null;

        return new DiffHighlight(oldSpans, newSpans);
    }

    private static (bool Old, bool New) NeededSides(DiffResult diff)
    {
        bool needOld = false, needNew = false;
        foreach (var h in diff.Hunks)
        {
            foreach (var l in h.Lines)
            {
                if (l.Kind == DiffLineKind.Removed) needOld = true;
                else needNew = true; // Added or Context both come from the new file
                if (needOld && needNew) return (true, true);
            }
        }
        return (needOld, needNew);
    }

    private static IReadOnlyList<IReadOnlyList<TokenSpan>>? HighlightSide(
        IGitService git, Repo repo, DiffResult diff, string? commitSha, string? baseSha, bool oldSide, string languageId)
    {
        // On the old side of a rename, the content lives at the pre-rename path.
        var path = oldSide && diff.OldPath != null ? diff.OldPath : diff.Path;
        var text = git.GetFileText(repo, path, diff.Side, oldSide, commitSha, baseSha);
        return text == null ? null : Highlighter.Highlight(text, languageId);
    }
}
