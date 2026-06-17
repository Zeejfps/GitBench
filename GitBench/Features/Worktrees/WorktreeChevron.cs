using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Widgets;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Worktrees;

// Chevron slot before a row's icon: a clickable fold toggle when the row has children, otherwise an
// empty slot that still reserves the width so rows stay aligned. Resolves the row's node view model
// from scope — expand state and child presence come from there.
public sealed record WorktreeChevron : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<RepoNodeViewModel>();
        var theme = ctx.Theme();

        return new KbmInput
        {
            OnClick = () => { if (vm.HasChildren.Value) vm.ToggleExpand(); },
            Child = new Box
            {
                Width = RepoBar.RowChevronWidth,
                Children =
                [
                    new Text
                    {
                        FontFamily = LucideIcons.FontFamily,
                        FontSize = 11f,
                        HAlign = TextAlignment.Center,
                        VAlign = TextAlignment.Center,
                        Width = RepoBar.RowChevronWidth,
                        Color = Prop.Bind(() => theme.Styles.Value.Palette.TextSecondary),
                        Value = Prop.Bind<string?>(() =>
                            !vm.HasChildren.Value ? string.Empty
                            : vm.IsExpanded.Value ? LucideIcons.ChevronDown : LucideIcons.ChevronRight),
                    },
                ],
            },
        };
    }
}
