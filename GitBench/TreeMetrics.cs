namespace GitGui;

/// <summary>
/// Shared indentation metrics for the three tree views — the RepoBar (groups → repos →
/// worktrees/submodules), <see cref="BranchesView"/> (sections → folders → branches), and
/// the Local Changes file panels (folders → files). Root-level content starts at
/// <see cref="BaseIndent"/> from the row's left edge; each nesting level adds
/// <see cref="IndentLevel"/>. Centralized so the trees render the same spacing and can't
/// drift apart.
/// </summary>
internal static class TreeMetrics
{
    public const float BaseIndent = 12f;
    public const float IndentLevel = 16f;
}
