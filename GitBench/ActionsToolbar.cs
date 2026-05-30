using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

internal sealed class ActionsToolbar : MultiChildView, IBind<ActionsToolbarViewModel>
{
    private const float ToolbarHeight = 44f;
    private const int HorizontalPadding = 8;
    private const float WithinClusterGap = 2f;

    private readonly ActionButton _pushButton;
    private readonly ActionButton _pullButton;
    private readonly ActionButton _fetchButton;
    private readonly ActionButton _branchButton;
    private readonly ActionButton _stashButton;
    private readonly ActionButton _openFolderButton;
    private readonly ActionButton _openTerminalButton;
    private readonly ActionButton _toggleThemeButton;
    private readonly ErrorBarView _errorBar;

    public ActionsToolbar()
    {
        Height = ToolbarHeight;

        _pushButton = new ActionButton(LucideIcons.Push, "Push", badgeColor: s => s.ActionsToolbar.BadgeAhead);
        _pullButton = new ActionButton(LucideIcons.Pull, "Pull", badgeColor: s => s.ActionsToolbar.BadgeBehind);
        _fetchButton = new ActionButton(LucideIcons.Fetch, "Fetch");
        _branchButton = new ActionButton(LucideIcons.Branch, "Branch");
        _stashButton = new ActionButton(LucideIcons.Stash, "Stash");
        _openFolderButton = new ActionButton(LucideIcons.FolderOpen, tooltip: "Open in file explorer");
        _openTerminalButton = new ActionButton(LucideIcons.SquareTerminal, tooltip: "Open in terminal");
        _toggleThemeButton = new ActionButton(LucideIcons.Sun, tooltip: "Toggle theme");

        _errorBar = new ErrorBarView(verticalPadding: 2);
        var contentRow = new FlexRowView
        {
            Gap = WithinClusterGap,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new ModeSwitcherView(),
                new SeparatorSpacer(),
                _fetchButton,
                _pullButton,
                _pushButton,
                new SeparatorSpacer(),
                _stashButton,
                _branchButton,
                new FlexItem { Grow = 1, Child = new MultiChildView() },
                _openFolderButton,
                _openTerminalButton,
                _toggleThemeButton,
                _errorBar,
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
        bar.BindThemedBackgroundColor(s => s.ActionsToolbar.Background);
        bar.BindThemedBorderColor(s => new BorderColorStyle { Bottom = s.ActionsToolbar.BorderBottom });
        AddChildToSelf(bar);

        this.UseViewModel(this);
    }

    public void Bind(ActionsToolbarViewModel vm)
    {
        _pushButton.BindCommand(vm.Push);
        _pullButton.BindCommand(vm.Pull);
        _fetchButton.BindCommand(vm.Fetch);
        _branchButton.BindCommand(vm.Branch);
        _stashButton.BindCommand(vm.Stash);
        _openFolderButton.BindCommand(vm.OpenFolder);
        _openTerminalButton.BindCommand(vm.OpenTerminal);
        _toggleThemeButton.BindCommand(vm.ToggleTheme);
        _toggleThemeButton.Icon.BindTo(vm.Theme, m => m == ThemeMode.Dark ? LucideIcons.Sun : LucideIcons.Moon);

        _pushButton.Badge.BindTo(vm.PushBadge);
        _pullButton.Badge.BindTo(vm.PullBadge);

        _pushButton.Icon.BindTo(vm.IsPushing, b => b ? LucideIcons.Loader : LucideIcons.Push);
        _pushButton.Label.BindTo(vm.IsPushing, b => b ? "Pushing" : "Push");
        _pushButton.IconRotation.BindTo(vm.PushRotation);

        _pullButton.Icon.BindTo(vm.IsPulling, b => b ? LucideIcons.Loader : LucideIcons.Pull);
        _pullButton.Label.BindTo(vm.IsPulling, b => b ? "Pulling" : "Pull");
        _pullButton.IconRotation.BindTo(vm.PullRotation);

        _fetchButton.Icon.BindTo(vm.IsFetching, b => b ? LucideIcons.Loader : LucideIcons.Fetch);
        _fetchButton.Label.BindTo(vm.IsFetching, b => b ? "Fetching" : "Fetch");
        _fetchButton.IconRotation.BindTo(vm.FetchRotation);

        _errorBar.Message.BindTo(vm.Error);
    }
}
