using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Widgets;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Worktrees;

// Slot that sits before the icon on every RepoBar row. When the repo has child rows
// (worktrees and/or nested submodules), it renders a clickable chevron that toggles their
// visibility without activating the repo. Otherwise it occupies the same horizontal space
// with nothing in it, so rows stay aligned whether or not children exist. Works for any
// repo — primaries AND submodules — so submodules-of-submodules get their own fold.
public sealed record WorktreeChevron : Widget
{
    public required Repo Repo { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var registry = ctx.Require<IRepoRegistry>();
        var theme = ctx.Theme();
        var expanded = registry.WatchWorktreeExpanded(repo.Id);

        return new KbmInput
        {
            OnClick = () =>
            {
                if (!HasChildren(repo.Id, registry)) return;
                registry.SetWorktreeExpanded(repo.Id, !expanded.Value);
            },
            Child = new Box
            {
                Width = RepoBar.RowChevronWidth,
                Children =
                [
                    new Text
                    {
                        FontFamily = LucideIcons.FontFamily,
                        FontSize = 11f,
                        HAlign = TextAlignment.Center,
                        VAlign = TextAlignment.Center,
                        Width = RepoBar.RowChevronWidth,
                        Color = Prop.Bind(() => theme.Styles.Value.Palette.TextSecondary),
                        Value = Prop.Bind<string?>(() =>
                            !HasChildren(repo.Id, registry) ? string.Empty
                            : expanded.Value ? LucideIcons.ChevronDown : LucideIcons.ChevronRight),
                    },
                ],
            },
        };
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
