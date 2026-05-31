using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Thin status bar spanning the full width of the window bottom (the outer border layout's
/// South region). Left side shows ambient repo context — active repo, current branch, and
/// ahead/behind counts; right side holds the theme toggle and the running build version.
/// </summary>
internal sealed class StatusBarView : MultiChildView, IBind<StatusBarViewModel>
{
    private const int BarHeight = 22;
    private const int HorizontalPadding = 8;

    private readonly FlexRowView _repoCluster;
    private readonly TextView _repoName;
    private readonly FlexRowView _branchCluster;
    private readonly TextView _branchName;
    private readonly FlexRowView _aheadCluster;
    private readonly TextView _aheadText;
    private readonly FlexRowView _behindCluster;
    private readonly TextView _behindText;
    private readonly StatusBarIconButton _themeButton;

    public StatusBarView()
    {
        // Fix the bar height here (not on the inner RectView): the inner view also carries a
        // 1px top border, so giving it an explicit Height would make its measured size exceed
        // its laid-out size by the border and leave a 1px gap above the bar. Sizing the outer
        // view and letting the RectView fill the region keeps it flush against the content.
        Height = BarHeight;

        (_repoCluster, _repoName) = Segment(LucideIcons.FolderGit2);
        (_branchCluster, _branchName) = Segment(LucideIcons.Branch);
        (_aheadCluster, _aheadText) = Segment(LucideIcons.ChevronUp);
        (_behindCluster, _behindText) = Segment(LucideIcons.ChevronDown);

        var left = new FlexRowView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { _repoCluster, _branchCluster, _aheadCluster, _behindCluster },
        };

        _themeButton = new StatusBarIconButton("Toggle theme");

        var version = new TextView
        {
            Text = $"v{AppVersion.Display}",
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.End,
        };
        version.BindThemedTextColor(s => s.StatusBar.Text);

        var bar = new RectView
        {
            BorderSize = new BorderSizeStyle { Top = 1 },
            Padding = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
            Children =
            {
                new FlexRowView
                {
                    Gap = 8,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children =
                    {
                        _themeButton,
                        new FlexItem { Grow = 1, Child = left },
                        version,
                    },
                },
            },
        };
        bar.BindThemedBackgroundColor(s => s.StatusBar.Background);
        bar.BindThemedBorderColor(s => new BorderColorStyle { Top = s.StatusBar.TopBorder });
        AddChildToSelf(bar);

        this.UseViewModel(this);
    }

    public void Bind(StatusBarViewModel vm)
    {
        _repoCluster.BindIsVisible(vm.HasActiveRepo, b => b);
        _repoName.BindText(vm.RepoName);

        _branchCluster.BindIsVisible(vm.HasBranch, b => b);
        _branchName.BindText(vm.Branch);

        _aheadCluster.BindIsVisible(vm.ShowAhead, b => b);
        _aheadText.BindText(vm.AheadText);

        _behindCluster.BindIsVisible(vm.ShowBehind, b => b);
        _behindText.BindText(vm.BehindText);

        _themeButton.BindCommand(vm.ToggleTheme);
        _themeButton.Icon.BindTo(vm.Theme, m => m == ThemeMode.Dark ? LucideIcons.Sun : LucideIcons.Moon);
    }

    // An icon glyph + label pair. Returns the row (for visibility binding) and the label
    // TextView (for text binding). Both glyph and label render in the muted status-bar color.
    private static (FlexRowView Row, TextView Label) Segment(string glyph)
    {
        var icon = new TextView
        {
            Text = glyph,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindThemedTextColor(s => s.StatusBar.Text);

        var label = new TextView
        {
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindThemedTextColor(s => s.StatusBar.Text);

        var row = new FlexRowView
        {
            Gap = 4,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { icon, label },
        };
        return (row, label);
    }
}
