using GitBench.Features.Branches;
using GitBench.Widgets;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// Shared indentation and row-rhythm metrics for the three tree views — the RepoBar (groups →
/// repos → worktrees/submodules), <see cref="BranchesView"/> (sections → folders → branches), and
/// the Local Changes file panels (folders → files). Root-level content starts at
/// <see cref="BaseIndent"/> from the row's left edge; each nesting level adds
/// <see cref="IndentLevel"/>. Within a row, the chevron and icon sit in fixed-width slots
/// (<see cref="ChevronWidth"/>, <see cref="IconWidth"/>) separated by <see cref="ColumnGap"/>, so a
/// glyph's natural width never shifts where the name starts. Centralized so the trees render the
/// same spacing and can't drift apart.
/// </summary>
internal static class TreeMetrics
{
    public const float BaseIndent = 12f;
    public const float IndentLevel = 16f;
    public const float ChevronWidth = 12f;
    public const float IconWidth = Sizes.Icon;
    public const float ColumnGap = 6f;
}
