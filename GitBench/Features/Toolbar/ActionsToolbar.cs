using GitBench.App;
using GitBench.Controls;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Toolbar;

internal sealed record ActionsToolbar : Widget
{
    private const float ToolbarHeight = 44f;
    private const int HorizontalPadding = 8;
    private const float WithinClusterGap = 2f;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ActionsToolbarViewModel>();
        var styles = ctx.Theme().Styles;

        return new Box
        {
            Height = ToolbarHeight,
            Background = styles.Bind(s => s.ActionsToolbar.Background),
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            BorderColor = styles.Bind(s => new BorderColorStyle { Bottom = s.ActionsToolbar.BorderBottom }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
                    Children =
                    [
                        new HorizontalScrollArea
                        {
                            Child = new Row
                            {
                                Gap = WithinClusterGap,
                                CrossAxis = CrossAxisAlignment.Center,
                                Children = BuildActions(vm),
                            },
                        },
                    ],
                },
            ],
        }.BindVm(vm);
    }

    private static IWidget[] BuildActions(ActionsToolbarViewModel vm) =>
    [
        new ModeSwitcherView(),
        new SeparatorSpacer(),
        new ToolbarSyncButton
        {
            Command = vm.Fetch,
            IsBusy = vm.IsFetching,
            Icon = LucideIcons.Fetch,
            Rotation = vm.FetchRotation,
            Label = L.T(s => vm.IsFetching.Value ? s.ToolbarFetching : s.ToolbarFetch),
        },
        new ToolbarSyncButton
        {
            Command = vm.Pull,
            IsBusy = vm.IsPulling,
            Icon = LucideIcons.Pull,
            Rotation = vm.PullRotation,
            Label = L.T(s => vm.IsPulling.Value ? s.ToolbarPulling : s.ToolbarPull),
            Badge = vm.PullBadge,
            BadgeAccent = s => s.ActionsToolbar.BadgeBehind,
        },
        new ToolbarSyncButton
        {
            Command = vm.Push,
            IsBusy = vm.IsPushing,
            Icon = LucideIcons.Push,
            Rotation = vm.PushRotation,
            Label = L.T(s => vm.IsPushing.Value ? s.ToolbarPushing : s.ToolbarPush),
            Badge = vm.PushBadge,
            BadgeAccent = s => s.ActionsToolbar.BadgeAhead,
        },
        new SeparatorSpacer(),
        new ToolbarButton { Command = vm.Stash, Icon = LucideIcons.Stash, Label = L.T(s => s.ToolbarStash) },
        new ToolbarButton { Command = vm.DiscardAll, Icon = LucideIcons.Trash, Label = L.T(s => s.ToolbarDiscardAll) },
        new SeparatorSpacer(),
        new ToolbarButton { Command = vm.Branch, Icon = LucideIcons.Branch, Label = L.T(s => s.ToolbarBranch) },
        new Spacer(),
        new ToolbarIconButton
        {
            Command = vm.OpenFolder,
            Icon = LucideIcons.FolderOpen,
            Tooltip = L.T(s => s.ToolbarOpenFolderTooltip),
        },
        new ToolbarIconButton
        {
            Command = vm.OpenTerminal,
            Icon = LucideIcons.SquareTerminal,
            Tooltip = L.T(s => s.ToolbarOpenTerminalTooltip),
        },
    ];
}
