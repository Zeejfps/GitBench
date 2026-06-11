using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// Repo-level banner shown whenever the active repo is on a detached HEAD whose commits sit
/// on no branch — so they're reachable only from HEAD and would be orphaned by a checkout.
/// Mounted above both the branches sidebar and the main content so it's visible regardless
/// of the active tab. Self-managing: toggles <see cref="View.IsVisible"/> off (collapsing its
/// layout slot in the surrounding FlexColumn) when there's nothing at risk. Not dismissible —
/// it clears automatically once the commits land on a branch.
/// </summary>
internal sealed record DetachedHeadBannerView : Widget
{
    protected override View CreateView(Context ctx)
    {
        var vm = new DetachedHeadBannerViewModel(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var theme = ctx.Theme();

        var text = new TextView(ctx.Canvas)
        {
            Text = "Detached HEAD — your latest commits aren't on any branch.",
            VerticalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.Wrap,
        };
        text.BindTextColor(() => theme.Styles.Value.Banner.Text);

        var createBranchButton = new ActionButton
        {
            Icon = LucideIcons.Branch,
            Label = "Create branch",
            Tooltip = "Create a branch here so these commits aren't lost",
            Background = 0xFF4E8B3D,
            Command = vm.CreateBranch,
        }.BuildView(ctx);

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
                        createBranchButton,
                    },
                },
            },
        };
        banner.BindBackgroundColor(() => theme.Styles.Value.Banner.Background);
        banner.BindBorderColor(() => new BorderColorStyle { Bottom = theme.Styles.Value.Banner.Border });
        banner.BindIsVisible(() => vm.IsAtRisk.Value);

        banner.UseViewModel(() => vm, _ => { });
        return banner;
    }
}
