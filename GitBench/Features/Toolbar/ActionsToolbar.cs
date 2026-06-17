using GitBench.App;
using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

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
            Padding = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
            Children =
            [
                new Row
                {
                    Gap = WithinClusterGap,
                    CrossAxis = CrossAxisAlignment.Center,
                    Children =
                    [
                        new ModeSwitcherView(),
                        new SeparatorSpacer(),
                        new ActionButton
                        {
                            Icon = Prop.Bind<string?>(() => vm.IsFetching.Value ? LucideIcons.Loader : LucideIcons.Fetch),
                            Label = Prop.Bind<string?>(() => vm.IsFetching.Value ? "Fetching" : "Fetch"),
                            IconRotation = Prop.Bind(vm.FetchRotation),
                            Command = vm.Fetch,
                        },
                        new ActionButton
                        {
                            Icon = Prop.Bind<string?>(() => vm.IsPulling.Value ? LucideIcons.Loader : LucideIcons.Pull),
                            Label = Prop.Bind<string?>(() => vm.IsPulling.Value ? "Pulling" : "Pull"),
                            IconRotation = Prop.Bind(vm.PullRotation),
                            BadgeColor = Theme.Color(s => s.ActionsToolbar.BadgeBehind),
                            Badge = vm.PullBadge,
                            Command = vm.Pull,
                        },
                        new ActionButton
                        {
                            Icon = Prop.Bind<string?>(() => vm.IsPushing.Value ? LucideIcons.Loader : LucideIcons.Push),
                            Label = Prop.Bind<string?>(() => vm.IsPushing.Value ? "Pushing" : "Push"),
                            IconRotation = Prop.Bind(vm.PushRotation),
                            BadgeColor = Theme.Color(s => s.ActionsToolbar.BadgeAhead),
                            Badge = vm.PushBadge,
                            Command = vm.Push,
                        },
                        new SeparatorSpacer(),
                        new ActionButton
                        {
                            Icon = LucideIcons.Stash,
                            Label = "Stash",
                            Command = vm.Stash,
                        },
                        new ActionButton
                        {
                            Icon = LucideIcons.Branch,
                            Label = "Branch",
                            Command = vm.Branch,
                        },
                        new Spacer(),
                        new ActionButton
                        {
                            Icon = LucideIcons.FolderOpen,
                            Tooltip = "Open in file explorer",
                            Command = vm.OpenFolder,
                        },
                        new ActionButton
                        {
                            Icon = LucideIcons.SquareTerminal,
                            Tooltip = "Open in terminal",
                            Command = vm.OpenTerminal,
                        },
                    ],
                },
            ],
        }.BindVm(vm);
    }
}
