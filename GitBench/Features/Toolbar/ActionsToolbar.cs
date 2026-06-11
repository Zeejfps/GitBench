using GitBench.App;
using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Toolbar;

internal sealed record ActionsToolbar : Widget
{
    private const float ToolbarHeight = 44f;
    private const int HorizontalPadding = 8;
    private const float WithinClusterGap = 2f;

    protected override View CreateView(Context ctx)
    {
        var vm = ctx.Require<ActionsToolbarViewModel>();
        var theme = ctx.Theme();

        var contentRow = new FlexRowView
        {
            Gap = WithinClusterGap,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new ModeSwitcherView().BuildView(ctx),
                new SeparatorSpacer().BuildView(ctx),
                new ActionButton
                {
                    Icon = LucideIcons.Fetch,
                    BindIcon = () => vm.IsFetching.Value ? LucideIcons.Loader : LucideIcons.Fetch,
                    BindLabel = () => vm.IsFetching.Value ? "Fetching" : "Fetch",
                    IconRotation = vm.FetchRotation,
                    Command = vm.Fetch,
                }.BuildView(ctx),
                new ActionButton
                {
                    Icon = LucideIcons.Pull,
                    BindIcon = () => vm.IsPulling.Value ? LucideIcons.Loader : LucideIcons.Pull,
                    BindLabel = () => vm.IsPulling.Value ? "Pulling" : "Pull",
                    IconRotation = vm.PullRotation,
                    BadgeColor = s => s.ActionsToolbar.BadgeBehind,
                    BindBadge = () => vm.PullBadge.Value,
                    Command = vm.Pull,
                }.BuildView(ctx),
                new ActionButton
                {
                    Icon = LucideIcons.Push,
                    BindIcon = () => vm.IsPushing.Value ? LucideIcons.Loader : LucideIcons.Push,
                    BindLabel = () => vm.IsPushing.Value ? "Pushing" : "Push",
                    IconRotation = vm.PushRotation,
                    BadgeColor = s => s.ActionsToolbar.BadgeAhead,
                    BindBadge = () => vm.PushBadge.Value,
                    Command = vm.Push,
                }.BuildView(ctx),
                new SeparatorSpacer().BuildView(ctx),
                new ActionButton
                {
                    Icon = LucideIcons.Stash,
                    Label = "Stash",
                    Command = vm.Stash,
                }.BuildView(ctx),
                new ActionButton
                {
                    Icon = LucideIcons.Branch,
                    Label = "Branch",
                    Command = vm.Branch,
                }.BuildView(ctx),
                new FlexItem { Grow = 1, Child = new ContainerView() },
                new ActionButton
                {
                    Icon = LucideIcons.FolderOpen,
                    Tooltip = "Open in file explorer",
                    Command = vm.OpenFolder,
                }.BuildView(ctx),
                new ActionButton
                {
                    Icon = LucideIcons.SquareTerminal,
                    Tooltip = "Open in terminal",
                    Command = vm.OpenTerminal,
                }.BuildView(ctx),
            }
        };

        var bar = new RectView
        {
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Padding = new PaddingStyle
            {
                Left = HorizontalPadding,
                Right = HorizontalPadding,
            },
            Children = { contentRow },
        };
        bar.BindBackgroundColor(() => theme.Styles.Value.ActionsToolbar.Background);
        bar.BindBorderColor(() => new BorderColorStyle { Bottom = theme.Styles.Value.ActionsToolbar.BorderBottom });

        var root = new ContainerView { Height = ToolbarHeight };
        root.Children.Add(bar);
        root.UseViewModel(() => vm, _ => { });
        return root;
    }
}
