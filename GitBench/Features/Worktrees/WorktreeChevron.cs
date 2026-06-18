using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Worktrees;

// Chevron slot before a row's icon: a clickable fold toggle when the row has children, otherwise an
// empty slot that still reserves the width so rows stay aligned. Resolves the row's node view model
// from scope — expand state and child presence come from there. Live press state lives on a
// ButtonState, so the parent attaches the controller (chevron.WithController<KbmController>()).
internal sealed record WorktreeChevron : Widget<ButtonState>
{
    protected override ButtonState CreateState(Context ctx) =>
        new(ctx.Require<RepoNodeViewModel>().ToggleExpand);

    protected override IWidget Build(Context ctx, ButtonState state)
    {
        var vm = ctx.Require<RepoNodeViewModel>();

        return new Box
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
                    Color = Theme.Color(s => s.Palette.TextSecondary),
                    Value = Prop.Bind<string?>(() =>
                        !vm.HasChildren.Value ? string.Empty
                        : vm.IsExpanded.Value ? LucideIcons.ChevronDown : LucideIcons.ChevronRight),
                },
            ],
        };
    }
}
