using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Review;

/// <summary>
/// Root widget hosted inside a review window. Phase 2 is a placeholder centred on the pinned range
/// (base ⟶ head); later phases replace the body with the header bar, the stack rail, and the reused
/// commit-details surface. Bound to the <see cref="ReviewWindowViewModel"/> supplied by the opening
/// <see cref="ReviewWindowsView"/>, which it also provides into the subtree.
/// </summary>
internal sealed record ReviewWindowRootView : Widget
{
    public required ReviewWindowViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var session = Model.Session;
        var baseLabel = session.BaseLabel ?? "auto";

        var content = new Box
        {
            Background = Theme.Color(s => s.Palette.Surface),
            Children =
            [
                new Center
                {
                    Child = new Text
                    {
                        Value = $"Review: {baseLabel} → {session.HeadLabel}",
                        FontSize = FontSize.Title,
                        HAlign = TextAlignment.Center,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.Palette.TextSecondary),
                    },
                },
            ],
        };

        return new Provide<ReviewWindowViewModel> { Value = Model, Child = content };
    }
}
