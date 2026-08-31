using GitBench.Features.CodeIntel;
using GitBench.Git;
using GitBench.Theming;

namespace GitBench.Features.Diff;

/// <summary>
/// Works out what one diff's file text means: fetches the needed side(s) once through
/// <see cref="IGitDiffReader"/>, then tokenizes and parses that text into a
/// <see cref="DiffAnnotations"/>. Pure orchestration with no threading of its own — the caller
/// (<see cref="DiffViewModel"/>) runs it on a background, generation-guarded lane so navigating
/// away discards stale results. Returns null when there is nothing to say about the diff.
/// </summary>
internal static class DiffAnnotationCoordinator
{
    /// <summary>Colors and outlines, for a surface that renders the diff.</summary>
    public static DiffAnnotations? Compute(
        ISymbolExtractor extractor, IGitDiffReader git, Repo repo, DiffResult diff, string? commitSha, string? baseSha = null)
        => Compute(extractor, git, repo, diff, commitSha, baseSha, HighlightLanguage(diff), StructureLanguage(diff));

    /// <summary>Outlines alone, for a caller that wants the hunk headers and would pay for
    /// tokenizing it never draws.</summary>
    public static DiffAnnotations? ComputeOutlines(
        ISymbolExtractor extractor, IGitDiffReader git, Repo repo, DiffResult diff, string? commitSha, string? baseSha = null)
        => Compute(extractor, git, repo, diff, commitSha, baseSha, languageId: null, StructureLanguage(diff));

    // The two outputs are gated separately: TextMate recognises languages tree-sitter has no
    // grammar for and the reverse is possible too, and each has its own kill switch. A file gets
    // colors, or contexts, or both.
    private static string? HighlightLanguage(DiffResult diff)
        => DiffOptions.SyntaxHighlightingEnabled ? LanguageRegistry.DetectLanguageId(diff.Path) : null;

    private static CodeLanguage? StructureLanguage(DiffResult diff)
        => DiffOptions.StructureEnabled ? CodeLanguages.Detect(diff.Path) : null;

    private static DiffAnnotations? Compute(
        ISymbolExtractor extractor, IGitDiffReader git, Repo repo, DiffResult diff,
        string? commitSha, string? baseSha, string? languageId, CodeLanguage? language)
    {
        if (diff.IsBinary || diff.ErrorMessage != null || diff.Hunks.Count == 0) return null;
        if (languageId == null && language == null) return null;

        // Only fetch the side(s) the diff actually shows: a pure-add diff has no removed rows
        // (skip the old blob), a pure-delete no added/context rows (skip the new blob).
        var (needOld, needNew) = NeededSides(diff);

        var oldText = needOld ? SideText(git, repo, diff, commitSha, baseSha, oldSide: true) : null;
        var newText = needNew ? SideText(git, repo, diff, commitSha, baseSha, oldSide: false) : null;
        if (oldText == null && newText == null) return null;

        var highlight = languageId == null ? null : Tokenize(oldText, newText, languageId);
        var oldOutline = language is { } l && oldText != null ? extractor.Extract(oldText, l) : null;
        var newOutline = language is { } n && newText != null ? extractor.Extract(newText, n) : null;
        if (highlight == null && oldOutline == null && newOutline == null) return null;

        return new DiffAnnotations(highlight, newOutline, oldOutline);
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

    private static string? SideText(
        IGitDiffReader git, Repo repo, DiffResult diff, string? commitSha, string? baseSha, bool oldSide)
    {
        // On the old side of a rename, the content lives at the pre-rename path.
        var path = oldSide && diff.OldPath != null ? diff.OldPath : diff.Path;
        return git.GetFileText(repo, path, diff.Side, oldSide, commitSha, baseSha);
    }

    private static DiffHighlight? Tokenize(string? oldText, string? newText, string languageId)
    {
        var highlighter = RoutedSyntaxHighlighter.Shared;
        var oldSpans = oldText == null ? null : highlighter.Highlight(oldText, languageId);
        var newSpans = newText == null ? null : highlighter.Highlight(newText, languageId);
        return oldSpans == null && newSpans == null ? null : new DiffHighlight(oldSpans, newSpans);
    }
}
