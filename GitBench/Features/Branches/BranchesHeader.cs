using GitBench.Controls;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Branches;

internal sealed record BranchesHeader : Widget
{
    private const float HeaderHeight = 44f;
    private const int HorizontalPadding = 8;

    protected override View CreateView(Context ctx)
    {
        var vm = ctx.Require<BranchesHeaderViewModel>();
        var theme = ctx.Theme();
        var view = Layout(vm, theme).BuildView(ctx);
        view.UseViewModel(() => vm, _ => { });
        return view;
    }

    private static IWidget Layout(BranchesHeaderViewModel vm, IThemeService<ThemeStyles> theme) => new Box
    {
        Height = HeaderHeight,
        BorderSize = new BorderSizeStyle { Bottom = 1 },
        Padding = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
        BindBackground = () => theme.Styles.Value.BranchesHeader.Background,
        BindBorder = () => new BorderColorStyle { Bottom = theme.Styles.Value.BranchesHeader.BorderBottom },
        Children =
        [
            new Row
            {
                CrossAxis = CrossAxisAlignment.Center,
                Children =
                [
                    new Box
                    {
                        Padding = new PaddingStyle { Left = 6, Right = 6 },
                        BindVisible = () => !string.IsNullOrEmpty(vm.BranchName.Value),
                        Children =
                        [
                            new Row
                            {
                                Gap = 6,
                                CrossAxis = CrossAxisAlignment.Stretch,
                                Children =
                                [
                                    new ThemedText
                                    {
                                        Value = LucideIcons.Branch,
                                        FontFamily = LucideIcons.FontFamily,
                                        FontSize = 15f,
                                        VAlign = TextAlignment.Center,
                                        Color = s => vm.IsDetached.Value ? s.BranchesHeader.DetachedText : s.BranchesHeader.ActiveText,
                                    },
                                    new ThemedText
                                    {
                                        Bind = () => vm.IsDetached.Value ? "at" : "on",
                                        VAlign = TextAlignment.Center,
                                        Color = s => s.BranchesHeader.PrefixText,
                                    },
                                    new ThemedText
                                    {
                                        Bind = () => vm.BranchName.Value,
                                        FontSize = 18f,
                                        Weight = FontWeight.Bold,
                                        VAlign = TextAlignment.Center,
                                        Color = s => vm.IsDetached.Value ? s.BranchesHeader.DetachedText : s.BranchesHeader.ActiveText,
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
        ],
    };
}
