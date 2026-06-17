using GitBench.Controls;
using GitBench.Features.Branches;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Submodules;

/// <summary>
/// Repo-level banner shown whenever the active repo has submodules whose working tree no
/// longer matches the commit the parent records (modified or conflicted). This is the safety
/// net for the gap left when `git pull --recurse-submodules` can't reconcile a dirty/conflicted
/// submodule. Mounted alongside <see cref="DetachedHeadBanner"/> so it's visible on any tab.
/// Self-managing: toggles <see cref="View.IsVisible"/> off (collapsing its layout slot) when no
/// submodule is out of date. Not dismissible — it clears once the submodules are updated.
/// </summary>
internal sealed record SubmoduleStatusBannerView : Widget
{
    protected override View CreateView(Context ctx)
    {
        var vm = new SubmoduleStatusBannerViewModel(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var theme = ctx.Theme();

        var text = new TextView(ctx.Canvas)
        {
            VerticalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.Wrap,
        };
        text.BindTextColor(() => theme.Styles.Value.Banner.Text);
        text.BindText(vm.OutdatedCount, n => n == 1
            ? "1 submodule is out of date — its working tree differs from the commit the main repo records."
            : $"{n} submodules are out of date — their working trees differ from the commits the main repo records.");

        var updateButton = new ActionButton
        {
            Icon = LucideIcons.Package,
            Label = "Update submodules",
            Tooltip = "Check submodules out to the commit the main repo records",
            Background = 0xFF4E8B3D,
            Command = vm.UpdateSubmodules,
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
                        updateButton,
                    },
                },
            },
        };
        banner.BindBackgroundColor(() => theme.Styles.Value.Banner.Background);
        banner.BindBorderColor(() => new BorderColorStyle { Bottom = theme.Styles.Value.Banner.Border });
        banner.BindIsVisible(() => vm.OutdatedCount.Value > 0);

        banner.UseViewModel(() => vm, _ => { });
        return banner;
    }
}
