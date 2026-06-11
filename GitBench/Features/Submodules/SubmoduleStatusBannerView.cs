using GitBench.Controls;
using GitBench.Features.Branches;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitBench.Features.Submodules;

/// <summary>
/// Repo-level banner shown whenever the active repo has submodules whose working tree no
/// longer matches the commit the parent records (modified or conflicted). This is the safety
/// net for the gap left when `git pull --recurse-submodules` can't reconcile a dirty/conflicted
/// submodule. Mounted alongside <see cref="DetachedHeadBannerView"/> so it's visible on any tab.
/// Self-managing: toggles <see cref="View.IsVisible"/> off (collapsing its layout slot) when no
/// submodule is out of date. Not dismissible — it clears once the submodules are updated.
/// </summary>
internal sealed class SubmoduleStatusBannerView : ContainerView, IBind<SubmoduleStatusBannerViewModel>
{
    private readonly ActionButton _updateButton;
    private readonly TextView _text;

    public SubmoduleStatusBannerView()
    {
        IsVisible = false;

        _text = new TextView(CompatUi.Canvas)
        {
            VerticalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.Wrap,
        };
        _text.BindThemedTextColor(s => s.Banner.Text);

        _updateButton = new ActionButton(
            LucideIcons.Package,
            "Update submodules",
            tooltip: "Check submodules out to the commit the main repo records",
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
                        new FlexItem { Grow = 1, Child = _text },
                        _updateButton,
                    },
                },
            },
        };
        banner.BindThemedBackgroundColor(s => s.Banner.Background);
        banner.BindThemedBorderColor(s => new BorderColorStyle { Bottom = s.Banner.Border });
        AddChildToSelf(banner);

        this.UseViewModel(this);
    }

    public void Bind(SubmoduleStatusBannerViewModel vm)
    {
        _updateButton.BindCommand(vm.UpdateSubmodules);
        _text.BindText(vm.OutdatedCount, n => n == 1
            ? "1 submodule is out of date — its working tree differs from the commit the main repo records."
            : $"{n} submodules are out of date — their working trees differ from the commits the main repo records.");
        vm.OutdatedCount.Subscribe(n => IsVisible = n > 0);
    }
}
