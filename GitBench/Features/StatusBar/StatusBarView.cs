using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Identity;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Thin status bar spanning the full width of the window bottom (the outer border layout's
/// South region). Left side shows ambient repo context — active repo, current branch, and
/// ahead/behind counts; right side holds the theme toggle and the running build version.
/// </summary>
internal sealed record StatusBarView : Widget
{
    private const int BarHeight = 22;
    private const int HorizontalPadding = 8;

    protected override View CreateView(Context ctx)
    {
        var vm = ctx.Require<StatusBarViewModel>();
        var theme = ctx.Theme();

        var (repoCluster, repoName) = Segment(ctx, LucideIcons.FolderGit2);
        var (branchCluster, branchName) = Segment(ctx, LucideIcons.Branch);
        var (aheadCluster, aheadText) = Segment(ctx, LucideIcons.ChevronUp);
        var (behindCluster, behindText) = Segment(ctx, LucideIcons.ChevronDown);
        var identityChip = new IdentityChipButton(ctx);

        var left = new FlexRowView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { repoCluster, branchCluster, aheadCluster, behindCluster, identityChip },
        };

        var themeButton = new StatusBarIconButton(ctx, "Toggle theme");
        var updateButton = new StatusBarIconButton(ctx, "Check for updates");

        // Brief inline result of a manual check ("up to date" / "failed"); hidden when empty.
        var updateFeedback = new TextView(ctx.Canvas)
        {
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.End,
        };
        updateFeedback.BindTextColor(() => theme.Styles.Value.StatusBar.Text);

        var version = new TextView(ctx.Canvas)
        {
            Text = $"v{AppVersion.Display}",
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.End,
        };
        version.BindTextColor(() => theme.Styles.Value.StatusBar.Text);

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
                        themeButton,
                        new FlexItem { Grow = 1, Child = left },
                        updateFeedback,
                        updateButton,
                        version,
                    },
                },
            },
        };
        bar.BindBackgroundColor(() => theme.Styles.Value.StatusBar.Background);
        bar.BindBorderColor(() => new BorderColorStyle { Top = theme.Styles.Value.StatusBar.TopBorder });

        // Fix the bar height here (not on the inner RectView): the inner view also carries a
        // 1px top border, so giving it an explicit Height would make its measured size exceed
        // its laid-out size by the border and leave a 1px gap above the bar. Sizing the outer
        // view and letting the RectView fill the region keeps it flush against the content.
        var root = new ContainerView { Height = BarHeight };
        root.Children.Add(bar);

        repoCluster.BindIsVisible(vm.HasActiveRepo, b => b);
        repoName.BindText(vm.RepoName);

        branchCluster.BindIsVisible(vm.HasBranch, b => b);
        branchName.BindText(vm.Branch);

        aheadCluster.BindIsVisible(vm.ShowAhead, b => b);
        aheadText.BindText(vm.AheadText);

        behindCluster.BindIsVisible(vm.ShowBehind, b => b);
        behindText.BindText(vm.BehindText);

        identityChip.BindIsVisible(vm.ShowIdentity, b => b);
        identityChip.Label.BindTo(vm.IdentityText);
        identityChip.Icon.BindTo(vm.IdentityGlyph);
        identityChip.MenuProvider = vm.BuildIdentityMenu;

        themeButton.BindCommand(vm.ToggleTheme);
        themeButton.Icon.BindTo(vm.Theme, m => m == ThemeMode.Dark ? LucideIcons.Sun : LucideIcons.Moon);

        updateButton.BindCommand(vm.CheckForUpdates);
        updateButton.Icon.BindTo(vm.IsCheckingUpdates, busy => busy ? LucideIcons.Loader : LucideIcons.Fetch);
        updateButton.IconRotation.BindTo(vm.UpdateIconRotation);

        updateFeedback.BindText(vm.UpdateCheckFeedback, m => m ?? string.Empty);
        updateFeedback.BindIsVisible(vm.UpdateCheckFeedback, m => !string.IsNullOrEmpty(m));

        root.UseViewModel(() => vm, _ => { });
        return root;
    }

    // An icon glyph + label pair. Returns the row (for visibility binding) and the label
    // TextView (for text binding). Both glyph and label render in the muted status-bar color.
    private static (FlexRowView Row, TextView Label) Segment(Context ctx, string glyph)
    {
        var theme = ctx.Theme();
        var icon = new TextView(ctx.Canvas)
        {
            Text = glyph,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindTextColor(() => theme.Styles.Value.StatusBar.Text);

        var label = new TextView(ctx.Canvas)
        {
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindTextColor(() => theme.Styles.Value.StatusBar.Text);

        var row = new FlexRowView
        {
            Gap = 4,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { icon, label },
        };
        return (row, label);
    }
}
