using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

// Visual bindings shared by RepoRow, WorktreeRow, and SubmoduleRow: the
// hover/active background fade and the missing/active/normal label colour selector.
// Each row still owns its icon + layout — only the colour decisions are centralised.
internal static class RowChrome
{
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
