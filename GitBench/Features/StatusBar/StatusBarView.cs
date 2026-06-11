using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Identity;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Thin status bar spanning the full width of the window bottom (the outer border layout's
/// South region). Left side shows ambient repo context — active repo, current branch, and
/// ahead/behind counts; right side holds the theme toggle and the running build version.
/// </summary>
internal sealed class StatusBarView : ContainerView, IBind<StatusBarViewModel>
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
    private readonly IdentityChipButton _identityChip;
    private readonly StatusBarIconButton _themeButton;
    private readonly StatusBarIconButton _updateButton;
    private readonly TextView _updateFeedback;

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
        _identityChip = new IdentityChipButton();

        var left = new FlexRowView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { _repoCluster, _branchCluster, _aheadCluster, _behindCluster, _identityChip },
        };

        _themeButton = new StatusBarIconButton("Toggle theme");
        _updateButton = new StatusBarIconButton("Check for updates");

        // Brief inline result of a manual check ("up to date" / "failed"); hidden when empty.
        _updateFeedback = new TextView(CompatUi.Canvas)
        {
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.End,
        };
        _updateFeedback.BindThemedTextColor(s => s.StatusBar.Text);

        var version = new TextView(CompatUi.Canvas)
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
                        _updateFeedback,
                        _updateButton,
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

        _identityChip.BindIsVisible(vm.ShowIdentity, b => b);
        _identityChip.Label.BindTo(vm.IdentityText);
        _identityChip.Icon.BindTo(vm.IdentityGlyph);
        _identityChip.MenuProvider = vm.BuildIdentityMenu;

        _themeButton.BindCommand(vm.ToggleTheme);
        _themeButton.Icon.BindTo(vm.Theme, m => m == ThemeMode.Dark ? LucideIcons.Sun : LucideIcons.Moon);

        _updateButton.BindCommand(vm.CheckForUpdates);
        _updateButton.Icon.BindTo(vm.IsCheckingUpdates, busy => busy ? LucideIcons.Loader : LucideIcons.Fetch);
        _updateButton.IconRotation.BindTo(vm.UpdateIconRotation);

        _updateFeedback.BindText(vm.UpdateCheckFeedback, m => m ?? string.Empty);
        _updateFeedback.BindIsVisible(vm.UpdateCheckFeedback, m => !string.IsNullOrEmpty(m));
    }

    // An icon glyph + label pair. Returns the row (for visibility binding) and the label
    // TextView (for text binding). Both glyph and label render in the muted status-bar color.
    private static (FlexRowView Row, TextView Label) Segment(string glyph)
    {
        var icon = new TextView(CompatUi.Canvas)
        {
            Text = glyph,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindThemedTextColor(s => s.StatusBar.Text);

        var label = new TextView(CompatUi.Canvas)
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
