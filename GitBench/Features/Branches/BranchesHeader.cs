using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed record BranchesHeader : Widget
{
    private const float HeaderHeight = 44f;
    private const int HorizontalPadding = 8;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<BranchesHeaderViewModel>();
        var theme = ctx.Theme();

        return new Box
        {
            Height = HeaderHeight,
            BorderSize = new BorderSizeStyle { Top = 0, Bottom = 1 },
            Padding = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
            Background = Prop.Bind(() => theme.Styles.Value.BranchesHeader.Background),
            BorderColor = Prop.Bind(() => new BorderColorStyle { Bottom = theme.Styles.Value.BranchesHeader.BorderBottom }),
            Children =
            [
                new Row
                {
                    CrossAxis = CrossAxisAlignment.Center,
                    Children =
                    [
                        new BranchLabel { BranchName = vm.BranchName, IsDetached = vm.IsDetached },
                    ],
                },
            ],
        }.BindVm(vm);
    }
}

/// <summary>Branch icon, "on"/"at" prefix, and branch name; hidden when there's no branch.</summary>
internal sealed record BranchLabel : Widget
{
    public required IReadable<string?> BranchName { get; init; }
    public required IReadable<bool> IsDetached { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Padding = new PaddingStyle { Left = 6, Right = 6 },
        BindVisible = () => !string.IsNullOrEmpty(BranchName.Value),
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
                        Color = s => IsDetached.Value ? s.BranchesHeader.DetachedText : s.BranchesHeader.ActiveText,
                    },
                    new ThemedText
                    {
                        Bind = () => IsDetached.Value ? "at" : "on",
                        VAlign = TextAlignment.Center,
                        Color = s => s.BranchesHeader.PrefixText,
                    },
                    new ThemedText
                    {
                        Bind = () => BranchName.Value,
                        FontSize = 18f,
                        Weight = FontWeight.Bold,
                        VAlign = TextAlignment.Center,
                        Color = s => IsDetached.Value ? s.BranchesHeader.DetachedText : s.BranchesHeader.ActiveText,
                    },
                ],
            },
        ],
    };
}
