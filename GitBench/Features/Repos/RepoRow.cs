using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// A single RepoBar row, rendered from its node view model. One widget for every kind — primary,
// worktree, submodule — with glyph, accent, indent, badge, and context menu all driven by the view
// model. Primaries drag-to-reorder; nested rows just activate on click.
internal sealed record RepoRow : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<RepoNodeViewModel>();
        var registry = ctx.Require<IRepoRegistry>();
        var input = ctx.Require<InputSystem>();
        var hovered = new State<bool>(false);

        var isPrimary = vm.Kind == RepoKind.Primary;
        var glyph = vm.Kind switch
        {
            RepoKind.Worktree => LucideIcons.Branch,
            RepoKind.Submodule => LucideIcons.Package,
            _ => LucideIcons.FolderGit2,
        };
        var leftPad = RepoBar.RowPaddingLeft + (int)TreeMetrics.IndentLevel * vm.Depth;

        // Primary icons share the label's missing/active/idle color; nested icons use the kind accent
        // (muted to the missing color when the checkout is gone).
        uint IconColor(ThemeStyles s)
        {
            if (isPrimary) return s.RepoBarRow.Text(vm.IsActive.Value, vm.IsMissing.Value);
            if (vm.IsMissing.Value) return s.RepoBarRow.TextMissing;
            return vm.Kind == RepoKind.Worktree
                ? s.RepoBarRow.IconAccentWorktree
                : s.RepoBarRow.IconAccentSubmodule;
        }

        IWidget row = new Box
        {
            Height = isPrimary ? 28 : 26,
            BorderRadius = BorderRadiusStyle.All(4),
            Background = Theme.Color(s => s.RepoBarRow.Background(vm.IsActive.Value, hovered.Value)),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = leftPad, Right = 12 },
                    Children =
                    [
                        new Row
                        {
                            Gap = RepoBar.RowIconGap,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new WorktreeChevron().WithController<KbmController>(),
                                new Text
                                {
                                    Value = glyph,
                                    FontFamily = LucideIcons.FontFamily,
                                    FontSize = isPrimary ? 14f : 13f,
                                    Width = RepoBar.RowIconWidth,
                                    HAlign = TextAlignment.Center,
                                    VAlign = TextAlignment.Center,
                                    Color = Theme.Color(IconColor),
                                },
                                new Grow
                                {
                                    Child = new Text
                                    {
                                        Value = Prop.Bind<string?>(() => vm.DisplayName.Value),
                                        HAlign = TextAlignment.Start,
                                        VAlign = TextAlignment.Center,
                                        Overflow = TextOverflow.Ellipsis,
                                        Color = Theme.Color(s => s.RepoBarRow.Text(vm.IsActive.Value, vm.IsMissing.Value)),
                                    },
                                },
                                new Box
                                {
                                    Width = 8,
                                    Height = 8,
                                    BorderRadius = BorderRadiusStyle.All(4),
                                    Background = Theme.Color(s => vm.Badge.Value == RepoRowBadge.Error
                                        ? s.RepoBarRow.BadgeError
                                        : s.RepoBarRow.BadgeDirty),
                                    Visible = Prop.Bind(() => vm.Badge.Value != RepoRowBadge.None),
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        return isPrimary
            ? row.WithController(input, view => new RepoRowController(
                view, ctx, vm.Repo, registry, vm.Activate, h => hovered.Value = h, _ => vm.BuildMenuItems()))
            : row.WithController(input, () => new NavigableRowController(
                ctx, vm.Activate, h => hovered.Value = h, _ => vm.BuildMenuItems()));
    }
}
