using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench;

// Visual bindings shared by RepoRow, WorktreeRow, and SubmoduleRow: the
// hover/active background fade, the missing/active/normal label colour selector, and the
// trailing status dot. Each row still owns its icon + layout — only the shared decisions
// are centralised.
internal static class RowChrome
{
    // Trailing status dot for any RepoBar row. Hidden when the repo has nothing to flag; red for an
    // unseen remote-op error (takes priority), else amber when the working tree is dirty. The store
    // reads are auto-tracked, so it appears/clears live. First of a planned badge family.
    public static RectView CreateBadge(IRepoStatusStore status, Guid repoId)
    {
        var dot = new RectView
        {
            Width = 8,
            Height = 8,
            BorderRadius = BorderRadiusStyle.All(4),
        };
        dot.BindThemedBackgroundColor(s => status.For(repoId).HasUnseenError
            ? s.RepoBarRow.BadgeError
            : s.RepoBarRow.BadgeDirty);
        dot.BindIsVisible(() =>
        {
            var st = status.For(repoId);
            return st.HasUnseenError || st.IsDirty;
        });
        return dot;
    }

    public static void BindRowBackground(RectView background, IReadable<bool> isHovered, IRepoRegistry registry, Guid rowId)
    {
        background.BindThemedBackgroundColor(s =>
        {
            var active = registry.Active.Value?.Id == rowId;
            return (isHovered.Value, active) switch
            {
                (_, true) => s.RepoBarRow.BackgroundActive,
                (true, false) => s.RepoBarRow.BackgroundHover,
                _ => s.RepoBarRow.BackgroundIdle,
            };
        });
    }

    public static void BindRowText(TextView view, IRepoRegistry registry, Repo row)
    {
        view.BindThemedTextColor(s =>
        {
            if (row.IsMissing) return s.RepoBarRow.TextMissing;
            return registry.Active.Value?.Id == row.Id
                ? s.RepoBarRow.TextActive
                : s.RepoBarRow.TextIdle;
        });
    }
}
