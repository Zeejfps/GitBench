using GitBench.Features.CodeIntel;
using GitBench.Git;

namespace GitBench.Features.Diff;

/// <summary>
/// One diff to load. BinaryText and NoCurrentVersionText are localized strings read on the UI
/// thread and carried in, because the loader runs on a worker and must not touch the localization
/// observable from there.
/// </summary>
internal sealed record DiffPreviewRequest(
    Repo Repo,
    DiffTarget Target,
    DiffViewMode Mode,
    bool Preview,
    string BinaryText,
    string NoCurrentVersionText);

/// <summary>
/// Turns a diff target into something the diff pane can draw: the patch, the whole after-file, or
/// the picture, markdown render or conflict header that stands in for one. The counterpart of
/// <see cref="FileBrowser.FileContentLoader"/> for diffs, and deliberately its shape — a render
/// leaves here complete, colors and outlines included, rather than arriving plain and turning
/// colored a beat later.
/// </summary>
/// <remarks>
/// Holds nothing but its services and touches no observable, so it is safe to call from any
/// worker. It has no cancellation of its own either: the caller runs it on a generation-guarded
/// lane, which drops the result of a load whose file the reader has already navigated away from.
/// </remarks>
internal sealed class DiffPreviewLoader(
    IGitDiffReader git, IGitConflictOperations conflicts, ISymbolExtractor extractor)
{
    /// <summary>The finished render. Never null: everything that cannot be drawn comes back as a
    /// <see cref="DiffRenderState.Placeholder"/> saying why.</summary>
    public DiffRenderState Load(DiffPreviewRequest request)
    {
        var (repo, target, mode, preview, _, _) = request;
        var (path, side, commitSha, baseSha) = (target.Path, target.Side, target.CommitSha, target.BaseSha);

        if (preview && MarkdownDiffPreview.IsPreviewablePath(path)
            && MarkdownDiffPreview.Build(git, repo, path, side, commitSha, baseSha) is { } markdown)
            return markdown;

        // A conflicted working-tree file gets the resolution header, not a normal diff — but only
        // in Diff mode. Toggling to FullFile escapes the header to show the raw working-tree file
        // (conflict markers and all). GetConflictContext is cheap (one `ls-files -u`) and returns
        // null for the common non-conflict case.
        if (side is DiffSide.Unstaged or DiffSide.WorkingTree && mode == DiffViewMode.Diff
            && conflicts.GetConflictContext(repo, path) is { } conflict)
            return new DiffRenderState.Conflict(path, conflict);

        // The diff is loaded either way: it supplies the added-line set for full-file tinting and
        // the removed rows the diff view colors from the before-side text.
        var diff = git.GetDiff(repo, path, side, commitSha, baseSha);
        // An image blob has no readable patch on either mode's terms, so the picture replaces the
        // body in both — the full-file toggle has nothing else to offer for it.
        if (BuildImage(repo, diff, path, side, commitSha, baseSha) is { } image) return image;

        return mode == DiffViewMode.Diff
            ? new DiffRenderState.Loaded(
                diff, DiffAnnotationCoordinator.Compute(extractor, git, repo, diff, commitSha, baseSha))
            : BuildFullFile(request, diff);
    }

    /// <summary>The after-side file as display lines, without a diff around it. For the lazy fetch
    /// behind the first gap-expander click, which wants the context the patch left out. Null when
    /// that side has no content (deleted underneath us).</summary>
    public List<string>? NewSideLines(Repo repo, DiffTarget target)
    {
        var text = git.GetFileText(repo, target.Path, target.Side, oldSide: false, target.CommitSha, target.BaseSha);
        return text == null ? null : SplitLines(text);
    }

    // Reads and decodes the blob behind a binary image file, or returns null to leave the diff
    // rendering as it did before (non-image path, unreadable/oversized blob, LFS pointer standing
    // in for the real bytes, a format neither codec handles).
    private DiffRenderState? BuildImage(
        Repo repo, DiffResult diff, string path, DiffSide side, string? commitSha, string? baseSha)
    {
        if (!diff.IsBinary || diff.ErrorMessage != null) return null;
        if (!ImagePreviewDecoder.IsPreviewablePath(path)) return null;

        var max = ImagePreviewDecoder.MaxSourceBytes;
        var isOldSide = false;
        var bytes = git.GetFileBytes(repo, path, side, oldSide: false, max, commitSha, baseSha);
        if (bytes == null)
        {
            // Deleted on this side: show what it looked like before rather than nothing.
            bytes = git.GetFileBytes(repo, path, side, oldSide: true, max, commitSha, baseSha);
            isOldSide = true;
        }
        if (bytes == null) return null;

        var preview = ImagePreviewDecoder.TryDecode(bytes);
        return preview == null
            ? null
            : new DiffRenderState.Image(path, preview, side, isOldSide, diff.IsLfs);
    }

    // Assembles a FullFile render from a loaded diff: fetches the after-side file text, caps it,
    // marks which lines were added, and annotates it from the text already in hand. Returns a
    // Placeholder for cases with no readable current version (binary, diff error, or a
    // deleted/absent file).
    private DiffRenderState BuildFullFile(DiffPreviewRequest request, DiffResult diff)
    {
        if (diff.IsBinary) return new DiffRenderState.Placeholder(request.BinaryText);
        if (diff.ErrorMessage != null) return new DiffRenderState.Placeholder(diff.ErrorMessage);

        var (repo, target) = (request.Repo, request.Target);
        var text = git.GetFileText(repo, target.Path, target.Side, oldSide: false, target.CommitSha, target.BaseSha);
        if (text == null) return new DiffRenderState.Placeholder(request.NoCurrentVersionText);

        var lines = SplitLines(text);
        var truncated = false;
        if (lines.Count > DiffOptions.TruncationLineCap)
        {
            lines.RemoveRange(DiffOptions.TruncationLineCap, lines.Count - DiffOptions.TruncationLineCap);
            truncated = true;
        }

        var added = new HashSet<int>();
        Dictionary<int, IReadOnlyList<CharRange>>? emphasis = null;
        foreach (var hunk in diff.Hunks)
        {
            // Intra-line pairing needs both sides; do it here while the diff is in hand, keyed by
            // new-line number so the after-file view can attach the new-side ranges per row.
            IReadOnlyList<CharRange>?[]? hunkEmphasis = null;
            if (DiffOptions.IntraLineHighlightingEnabled)
            {
                var expanded = new string[hunk.Lines.Count];
                for (var i = 0; i < hunk.Lines.Count; i++)
                    expanded[i] = DiffText.ExpandTabs(hunk.Lines[i].Text);
                hunkEmphasis = IntraLineDiff.ForHunk(hunk.Lines, expanded);
            }
            for (var i = 0; i < hunk.Lines.Count; i++)
            {
                var line = hunk.Lines[i];
                if (line.Kind != DiffLineKind.Added || line.NewLineNumber is not int n) continue;
                added.Add(n);
                if (hunkEmphasis?[i] is { Count: > 0 } ranges)
                    (emphasis ??= new Dictionary<int, IReadOnlyList<CharRange>>())[n] = ranges;
            }
        }

        return new DiffRenderState.FullFile(
            target.Path, lines, added, target.Side, truncated, emphasis,
            DiffAnnotationCoordinator.ComputeNewSide(extractor, diff, text));
    }

    // Splits file text into display lines, normalizing CRLF/CR to LF and dropping the single empty
    // element a trailing newline produces (so a file ending in "\n" doesn't show a phantom row).
    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = new List<string>(normalized.Split('\n'));
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
