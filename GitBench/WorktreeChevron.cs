using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

// Slot that sits before the icon on every primary RepoRow. When the primary has child
// rows (worktrees and/or submodules), it renders a clickable chevron that toggles their
// visibility without activating the repo. Otherwise it occupies the same horizontal
// space with nothing in it, so rows stay aligned whether or not children exist.
public sealed class WorktreeChevron : MultiChildView
{
    public WorktreeChevron(Repo primary, IRepoRegistry registry)
    {
        Width = RepoBar.RowChevronWidth;

        // Only primaries own children; child rows draw a blank slot for alignment.
        if (!primary.IsPrimary)
        {
            AddChildToSelf(new RectView { Width = RepoBar.RowChevronWidth });
            return;
        }

        var chevron = new TextView
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 11f,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            Width = RepoBar.RowChevronWidth,
        };
        chevron.BindThemedTextColor(s => s.Palette.TextSecondary);
        // Reads of registry.Repos and the WorktreesChanged version are auto-tracked, so
        // the chevron updates whenever children appear/disappear or expand state flips.
        chevron.BindText(() =>
        {
            _ = registry.WorktreesChanged.Value;
            if (!HasChildren(primary.Id, registry)) return string.Empty;
            return registry.IsWorktreeExpanded(primary.Id) ? LucideIcons.ChevronDown : LucideIcons.ChevronRight;
        });

        var background = new RectView
        {
            Width = RepoBar.RowChevronWidth,
            Children = { chevron },
        };
        AddChildToSelf(background);

        this.UseController(_ => new HoverableButtonController(
            () =>
            {
                if (!HasChildren(primary.Id, registry)) return;
                registry.SetWorktreeExpanded(primary.Id, !registry.IsWorktreeExpanded(primary.Id));
            },
            _ => { }));
    }

    private static bool HasChildren(Guid primaryId, IRepoRegistry registry)
    {
        foreach (var r in registry.Repos)
        {
            if (r.ParentRepoId == primaryId) return true;
        }
        return false;
    }
}
