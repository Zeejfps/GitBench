using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;

namespace GitBench.Features.Worktrees;

// Slot that sits before the icon on every RepoBar row. When the repo has child rows
// (worktrees and/or nested submodules), it renders a clickable chevron that toggles their
// visibility without activating the repo. Otherwise it occupies the same horizontal space
// with nothing in it, so rows stay aligned whether or not children exist. Works for any
// repo — primaries AND submodules — so submodules-of-submodules get their own fold.
public sealed class WorktreeChevron : MultiChildView
{
    public WorktreeChevron(Repo repo, IRepoRegistry registry)
    {
        Width = RepoBar.RowChevronWidth;

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
        // No children → empty glyph (the slot still reserves its width for alignment).
        chevron.BindText(() =>
        {
            _ = registry.WorktreesChanged.Value;
            if (!HasChildren(repo.Id, registry)) return string.Empty;
            return registry.IsWorktreeExpanded(repo.Id) ? LucideIcons.ChevronDown : LucideIcons.ChevronRight;
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
                if (!HasChildren(repo.Id, registry)) return;
                registry.SetWorktreeExpanded(repo.Id, !registry.IsWorktreeExpanded(repo.Id));
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
