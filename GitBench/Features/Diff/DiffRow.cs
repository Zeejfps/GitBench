using GitBench.Git;
using GitBench.Theming;

namespace GitBench.Features.Diff;

/// <summary>
/// Flat row stream the virtualized content view walks. Banners (rename/mode/truncated),
/// hunk separators, and individual diff lines all share a uniform row height so visible-range
/// math is trivial (floor/ceil on scrollY÷rowHeight).
/// </summary>
/// <summary>
/// Expander state for the gap a separator bar bridges: which expander icons it shows and how
/// many lines stay hidden (null while the EOF gap's count is unknown, which also omits the
/// "hidden lines" label). A separator with a null <see cref="DiffRow.HunkSeparator.Gap"/> is a
/// plain bar with no expanders.
/// </summary>
internal sealed record GapBar(int GapIndex, bool ShowDown, bool ShowUp, bool ShowUnfold, int? HiddenCount);

internal abstract record DiffRow
{
    public sealed record Banner(string Text) : DiffRow;
    // Range is empty for the trailing EOF bar, which draws no "@@" text.
    public sealed record HunkSeparator(string Range, string? Header, GapBar? Gap = null) : DiffRow;
    /// <summary>
    /// Pre-formatted strings (line numbers stringified, tabs expanded) so per-frame draw
    /// work doesn't allocate. <see cref="Spans"/> carries syntax-highlight color runs in the
    /// same tab-expanded column space as <see cref="Text"/>; null/empty means plain rendering.
    /// <see cref="Emphasis"/> carries intra-line changed-character ranges in that same column
    /// space (a background concern, separate from the foreground <see cref="Spans"/>); null for
    /// context lines, unpaired adds/removes, and full rewrites.
    /// </summary>
    public sealed record Line(
        DiffLineKind Kind,
        string OldNumber,
        string NewNumber,
        string Text,
        int Chars,
        IReadOnlyList<TokenSpan>? Spans = null,
        IReadOnlyList<CharRange>? Emphasis = null) : DiffRow;
}