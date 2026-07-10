using GitBench.Controls;
using GitBench.Features.Review;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// "N / M files staged" with a meter, or a success badge once everything is staged. Sits at the top of
/// the commit bar in the Review layout, where the reviewer is heading anyway: the walk down the stacked
/// diffs ends here, and the meter says how close the commit is to capturing everything. Hidden in the
/// List layout, whose panel headers already carry per-side counts, and while the working tree is clean.
/// </summary>
internal sealed record StagingProgressRow : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var layout = ctx.Require<State<WorkingChangesLayout>>();
        var review = ctx.Require<WorkingTreeReviewViewModel>();

        var visible = Prop.Bind(() =>
            layout.Value == WorkingChangesLayout.Review && review.Hud.Value.HasFiles);

        return new Row
        {
            Visible = visible,
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Row
                {
                    Gap = Spacing.Sm,
                    CrossAxis = CrossAxisAlignment.Center,
                    Visible = Prop.Bind(() => !review.Hud.Value.IsComplete),
                    Children =
                    [
                        new ReviewProgressMeter
                        {
                            Fraction = review.FilesFraction,
                            Fill = Theme.Color(s => s.Status.Success),
                        },
                        new Text
                        {
                            Value = Prop.Bind(review.FilesStagedLabel),
                            FontSize = FontSize.Caption,
                            Color = Theme.Color(s => s.Palette.TextSecondary),
                            VAlign = TextAlignment.Center,
                        },
                    ],
                },
                new Row
                {
                    Gap = Spacing.Xs,
                    CrossAxis = CrossAxisAlignment.Center,
                    Visible = Prop.Bind(() => review.Hud.Value.IsComplete),
                    Children =
                    [
                        new Text
                        {
                            FontFamily = LucideIcons.FontFamily,
                            FontSize = FontSize.Body,
                            Value = LucideIcons.CircleCheck,
                            Color = Theme.Color(s => s.Status.Success),
                            VAlign = TextAlignment.Center,
                        },
                        new Text
                        {
                            Value = L.T(s => s.ReviewAllStaged),
                            FontSize = FontSize.Caption,
                            Color = Theme.Color(s => s.Status.Success),
                            VAlign = TextAlignment.Center,
                        },
                    ],
                },
                new Grow { Child = new Box() },
            ],
        };
    }
}
