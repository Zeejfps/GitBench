using GitBench.App;
using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
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
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
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
                                    Icon = vm.IsFetching.Bind(string? (f) => f ? LucideIcons.Loader : LucideIcons.Fetch),
                                    Label = vm.IsFetching.Bind(string? (f) => f ? "Fetching" : "Fetch"),
                                    IconRotation = Prop.Bind(vm.FetchRotation),
                                    Command = vm.Fetch,
                                }.WithController<KbmController>(),
                                new ActionButton
                                {
                                    Icon = vm.IsPulling.Bind(string? (p) => p ? LucideIcons.Loader : LucideIcons.Pull),
                                    Label = vm.IsPulling.Bind(string? (p) => p ? "Pulling" : "Pull"),
                                    IconRotation = Prop.Bind(vm.PullRotation),
                                    BadgeColor = Theme.Color(s => s.ActionsToolbar.BadgeBehind),
                                    Badge = Prop.Bind(vm.PullBadge),
                                    Command = vm.Pull,
                                }.WithController<KbmController>(),
                                new ActionButton
                                {
                                    Icon = vm.IsPushing.Bind(string? (p) => p ? LucideIcons.Loader : LucideIcons.Push),
                                    Label = vm.IsPushing.Bind(string? (p) => p ? "Pushing" : "Push"),
                                    IconRotation = Prop.Bind(vm.PushRotation),
                                    BadgeColor = Theme.Color(s => s.ActionsToolbar.BadgeAhead),
                                    Badge = Prop.Bind(vm.PushBadge),
                                    Command = vm.Push,
                                }.WithController<KbmController>(),
                                new SeparatorSpacer(),
                                new ActionButton
                                {
                                    Icon = LucideIcons.Stash,
                                    Label = "Stash",
                                    Command = vm.Stash,
                                }.WithController<KbmController>(),
                                new ActionButton
                                {
                                    Icon = LucideIcons.Branch,
                                    Label = "Branch",
                                    Command = vm.Branch,
                                }.WithController<KbmController>(),
                                new Spacer(),
                                new ActionButton
                                {
                                    Icon = LucideIcons.FolderOpen,
                                    Tooltip = "Open in file explorer",
                                    Command = vm.OpenFolder,
                                }.WithController<KbmController>(),
                                new ActionButton
                                {
                                    Icon = LucideIcons.SquareTerminal,
                                    Tooltip = "Open in terminal",
                                    Command = vm.OpenTerminal,
                                }.WithController<KbmController>(),
                            ],
                        },
                    ],
                },
            ],
        }.BindVm(vm);
    }
}
