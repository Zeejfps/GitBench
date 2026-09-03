using GitBench.Git;
using GitBench.Theming;

namespace GitBench.Features.Diff;

/// <summary>
/// Flat row stream the virtualized content view walks: banners (rename/mode/truncated), hunk
/// separators, individual diff lines, and the code-vision rows annotating declarations.
/// <see cref="DiffRowMetrics"/> is what says how tall each one draws.
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
    /// <summary>The torn break between a large gap's two expander bars: plain background with a
    /// jagged tear line, the unfold-all icon, and the hidden-line count. <see cref="Gap"/> always
    /// has <c>ShowUnfold</c> set and an exact <c>HiddenCount</c>.</summary>
    public sealed record Tear(GapBar Gap) : DiffRow;
    /// <summary>
    /// Pre-formatted for drawing (tabs expanded) so per-frame draw work doesn't allocate.
    /// <see cref="Text"/> keeps the raw file line beside its expansion, so the clipboard and the
    /// assistant can have the characters the file actually holds; <see cref="OldNumber"/> and
    /// <see cref="NewNumber"/> keep each gutter's <see cref="FileLine"/> beside its digits, so
    /// nothing has to read a line number back out of the gutter text.
    /// <see cref="Spans"/> carries syntax-highlight color runs in the tab-expanded column space;
    /// null/empty means plain rendering. <see cref="Emphasis"/> carries intra-line
    /// changed-character ranges in that same column space (a background concern, separate from the
    /// foreground <see cref="Spans"/>); null for context lines, unpaired adds/removes, and full
    /// rewrites. <see cref="Fold"/> is set only on the two rows a foldable declaration touches.
    /// </summary>
    public sealed record Line(
        DiffLineKind Kind,
        DiffGutterNumber OldNumber,
        DiffGutterNumber NewNumber,
        DiffLineText Text,
        IReadOnlyList<TokenSpan>? Spans = null,
        IReadOnlyList<CharRange>? Emphasis = null,
        FoldMark? Fold = null) : DiffRow;
    /// <summary>
    /// The usages row above a declaration. It carries no text: the rows are flattened the moment
    /// the file is parsed, and the counts arrive one declaration at a time long afterwards, so what
    /// it says comes from <see cref="UsageLensOverlay"/> at draw time. What is fixed at flatten
    /// time is where it points and where it sits — the declaration's first line (past any
    /// attribute or decorator), the containment path folds key on so both features name a
    /// declaration the same way, and its indent in tab-expanded cells, so the lens lines up with
    /// the signature under it.
    /// </summary>
    /// <remarks>
    /// <see cref="NameLine"/> and <see cref="NameColumn"/> are where the declaration's own name is
    /// written, which is a different place from <see cref="At"/> — a server answers about the
    /// identifier, and asking at the start of the line lands on <c>public static async</c>. Carried
    /// here rather than looked up again, so nothing downstream needs the outline to ask the
    /// question, and there is no id that could fail to resolve back to a declaration.
    /// </remarks>
    public sealed record Lens(
        FileLine At, string Id, int Indent, FileLine NameLine, RawColumn NameColumn) : DiffRow;
}