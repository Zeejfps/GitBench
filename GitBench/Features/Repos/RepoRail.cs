using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Desktop.Widgets;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>
/// The collapsed repo sidebar: a narrow rail of repo tiles for switching repos while the full
/// bar is hidden, with the expand button up top.
/// </summary>
internal sealed record RepoRail : Widget
{
    internal const int RailWidth = 52;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<RepoRailViewModel>();
        var collapse = ctx.Require<RepoBarCollapseState>();

        // No ScrollArea: its always-reserved 12px scrollbar gutter would eat the rail's width and
        // shove the tiles off-center. The tiles wheel-scroll in a bare pane instead.
        var pane = new VerticalScrollPane { FillParent = true };
        pane.Children.Add(new Padding
        {
            Amount = new PaddingStyle { Top = Spacing.Md, Bottom = Spacing.Md },
            Children =
            [
                new Each<RailSectionViewModel>
                {
                    Items = vm.Sections,
                    Template = new RailSection(),
                    Gap = Spacing.Md,
                    CrossAxis = CrossAxisAlignment.Stretch,
                },
            ],
        }.BuildView(ctx));

        // The content/rail separator is a real 1px element rather than a Right border: flex rows
        // mirror under RTL, so it lands on the content-facing edge either way — Box border edges
        // don't mirror.
        return new Row
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new Box
                {
                    Width = RailWidth - 1,
                    Background = Theme.Color(s => s.RepoBar.Background),
                    Children =
                    [
                        new Column
                        {
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                BuildHeader(ctx, collapse),
                                new Grow
                                {
                                    Child = new KbmInput
                                    {
                                        Controller = _ => new RailWheelController(pane),
                                        Child = new Raw { View = pane },
                                    },
                                },
                                BuildFooter(ctx),
                            ],
                        },
                    ],
                },
                new Box
                {
                    Width = 1,
                    Background = Theme.Color(s => s.RepoBar.RightBorder),
                },
            ],
        }.BindVm(vm);
    }

    // Pinned add-repo tile under the scrolling list: shaped like the repo tiles, opens the same
    // open/clone menu as the expanded bar's header button.
    private static IWidget BuildFooter(Context ctx) => new Padding
    {
        Amount = new PaddingStyle { Top = Spacing.Sm, Bottom = Spacing.Md },
        Children =
        [
            new Column
            {
                Gap = Spacing.Sm,
                CrossAxis = CrossAxisAlignment.Center,
                Children =
                [
                    new Box
                    {
                        Width = 28,
                        Height = 1,
                        Background = Theme.Color(s => s.RepoBar.RightBorder),
                    },
                    new IconButtonWidget
                    {
                        Icon = LucideIcons.Plus,
                        IconSize = 16f,
                        Width = RepoRailTile.TileSize,
                        Height = RepoRailTile.TileSize,
                        CornerRadius = BorderRadiusStyle.All(Radius.Lg),
                        Surface = st => Theme.Color(t => st.Enabled.Value && st.Hovered.Value
                            ? t.Palette.Accent
                            : t.Palette.SurfaceHoverStrong),
                        Foreground = st => Theme.Color(t => st.Enabled.Value && st.Hovered.Value
                            ? t.Palette.TextOnAccent
                            : t.Palette.TextSecondary),
                    }
                        .WithTooltip(L.T(s => s.ReposAddButton))
                        .WithMenuController(rect =>
                            RepoBarContextMenu.Show(ctx, rect.TopLeft, AddRepoMenu.Items(ctx), MenuPlacement.Above)),
                ],
            },
        ],
    };

    // Consumes the wheel only when the pane actually moved, letting edge scrolls bubble out.
    private sealed class RailWheelController(VerticalScrollPane pane) : KeyboardMouseController
    {
        public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
        {
            if (e.Phase != EventPhase.Bubbling) return;
            if (pane.Scroll(e.DeltaY * -Scrolling.WheelStep))
                e.Consume();
        }
    }

    // Mirrors the expanded bar's header strip so the chrome lines up across the swap.
    private static IWidget BuildHeader(Context ctx, RepoBarCollapseState collapse) => new Box
    {
        Height = 44,
        BorderSize = new BorderSizeStyle { Bottom = 1 },
        BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.RepoBar.RightBorder }),
        Children =
        [
            new Row
            {
                MainAxis = MainAxisAlignment.Center,
                CrossAxis = CrossAxisAlignment.Center,
                Children =
                [
                    new IconButtonWidget
                    {
                        Icon = Direction.Glyph(ctx, LucideIcons.PanelLeftOpen, LucideIcons.PanelLeftClose),
                        IconSize = 15f,
                        Width = 24,
                        Height = 24,
                        Command = new Command(collapse.Toggle),
                        Surface = s => Theme.Color(t => t.HeaderActionButton.Surface(s)),
                        Foreground = s => Theme.Color(t => t.HeaderActionButton.Icon(s)),
                    }
                        .WithTooltip(L.T(s => s.ReposExpandButton))
                        .WithController<KbmController>(),
                ],
            },
        ],
    };
}

// One group's tiles in the rail; non-leading sections draw a divider where their group header
// would be.
internal sealed record RailSection : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<RailSectionViewModel>();
        return new Column
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Box
                {
                    Width = 28,
                    Height = 1,
                    Background = Theme.Color(s => s.RepoBar.RightBorder),
                    Visible = Prop.Bind(() => !vm.IsFirst.Value),
                },
                new Each<RepoNodeViewModel>
                {
                    Items = vm.Primaries,
                    Template = new RepoRailTile().WithController<NavigableRowController>(),
                    Gap = Spacing.Xs,
                    CrossAxis = CrossAxisAlignment.Center,
                },
            ],
        };
    }
}
