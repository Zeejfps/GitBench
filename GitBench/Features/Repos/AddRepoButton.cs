using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

// The outline "+ Add Repository" button at the bottom of the sidebar — just the look. The owner
// (RepoBar) bolts on the menu with WithMenuController.
internal sealed record AddRepoButton : Widget<ButtonState>
{
    protected override ButtonState CreateState(Context ctx) => new();

    protected override IWidget Build(Context ctx, ButtonState state) => new BorderedButtonSurface
    {
        State = state,
        Height = 30,
        Children =
        [
            new Text
            {
                Value = "+  Add Repository",
                HAlign = TextAlignment.Center,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.Palette.TextSecondary),
            },
        ],
    };
}
