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
                    Icon = Prop.Bind<string?>(() => vm.IsFetching.Value ? LucideIcons.Loader : LucideIcons.Fetch),
                    Label = Prop.Bind<string?>(() => vm.IsFetching.Value ? "Fetching" : "Fetch"),
                    IconRotation = Prop.Bind(vm.FetchRotation),
                    Command = vm.Fetch,
                }.BuildView(ctx),
                new ActionButton
                {
                    Icon = Prop.Bind<string?>(() => vm.IsPulling.Value ? LucideIcons.Loader : LucideIcons.Pull),
                    Label = Prop.Bind<string?>(() => vm.IsPulling.Value ? "Pulling" : "Pull"),
                    IconRotation = Prop.Bind(vm.PullRotation),
                    BadgeColor = Theme.Color(s => s.ActionsToolbar.BadgeBehind),
                    Badge = Prop.Bind(vm.PullBadge),
                    Command = vm.Pull,
                }.BuildView(ctx),
                new ActionButton
                {
                    Icon = Prop.Bind<string?>(() => vm.IsPushing.Value ? LucideIcons.Loader : LucideIcons.Push),
                    Label = Prop.Bind<string?>(() => vm.IsPushing.Value ? "Pushing" : "Push"),
                    IconRotation = Prop.Bind(vm.PushRotation),
                    BadgeColor = Theme.Color(s => s.ActionsToolbar.BadgeAhead),
                    Badge = Prop.Bind(vm.PushBadge),
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
