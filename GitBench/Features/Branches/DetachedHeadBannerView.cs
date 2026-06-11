using GitBench.Controls;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Views;

namespace GitBench.Features.Branches;

/// <summary>
/// Repo-level banner shown whenever the active repo is on a detached HEAD whose commits sit
/// on no branch — so they're reachable only from HEAD and would be orphaned by a checkout.
/// Mounted above both the branches sidebar and the main content so it's visible regardless
/// of the active tab. Self-managing: toggles <see cref="View.IsVisible"/> off (collapsing its
/// layout slot in the surrounding FlexColumn) when there's nothing at risk. Not dismissible —
/// it clears automatically once the commits land on a branch.
/// </summary>
internal sealed class DetachedHeadBannerView : ContainerView, IBind<DetachedHeadBannerViewModel>
{
    private readonly ActionButton _createBranchButton;

    public DetachedHeadBannerView()
    {
        IsVisible = false;

        var text = new TextView(CompatUi.Canvas)
        {
            Text = "Detached HEAD — your latest commits aren't on any branch.",
            VerticalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.Wrap,
        };
        text.BindThemedTextColor(s => s.Banner.Text);

        _createBranchButton = new ActionButton(
            LucideIcons.Branch,
            "Create branch",
            tooltip: "Create a branch here so these commits aren't lost",
            backgroundColor: 0xFF4E8B3D);

        var banner = new RectView
        {
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Padding = new PaddingStyle { Left = 12, Right = 12, Top = 6, Bottom = 6 },
            Children =
            {
                new FlexRowView
                {
                    Gap = 8,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children =
                    {
                        new FlexItem { Grow = 1, Child = text },
                        _createBranchButton,
                    },
                },
            },
        };
        banner.BindThemedBackgroundColor(s => s.Banner.Background);
        banner.BindThemedBorderColor(s => new BorderColorStyle { Bottom = s.Banner.Border });
        AddChildToSelf(banner);

        this.UseViewModel(this);
    }

    public void Bind(DetachedHeadBannerViewModel vm)
    {
        _createBranchButton.BindCommand(vm.CreateBranch);
        vm.IsAtRisk.Subscribe(atRisk => IsVisible = atRisk);
    }
}
