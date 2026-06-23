using GitBench.Features.Diff;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Small pill that reports a binary file's storage: "Git LFS" when the blob is tracked by Git LFS,
/// "Not in LFS" when it's committed inline. It hides entirely for non-binary files
/// (<see cref="LfsBadge.None"/>). The owner drives it by binding <see cref="Status"/> to the diff's
/// LFS state.
/// </summary>
internal sealed record LfsBadgeWidget : Widget
{
    /// <summary>The blob's LFS storage; <see cref="LfsBadge.None"/> hides the pill.</summary>
    public required Prop<LfsBadge> Status { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var theme = ctx.Theme();
        var loc = ctx.Localization();
        var status = Status.ToReadable(ctx);

        return new Box
        {
            Height = 16f,
            BorderRadius = BorderRadiusStyle.All(8),
            Visible = status.Bind(s => s != LfsBadge.None),
            Background = Prop.Bind(() => status.Value == LfsBadge.Tracked
                ? theme.Styles.Value.DiffView.LfsBadgeTrackedBackground
                : theme.Styles.Value.DiffView.LfsBadgeUntrackedBackground),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = 7, Right = 7 },
                    Children =
                    [
                        new Text
                        {
                            FontSize = 10f,
                            HAlign = TextAlignment.Center,
                            VAlign = TextAlignment.Center,
                            Value = Prop.Bind<string?>(() =>
                            {
                                var strings = loc.Strings.Value;
                                return status.Value switch
                                {
                                    LfsBadge.Tracked => strings.StatusbarLfsTracked,
                                    LfsBadge.NotTracked => strings.StatusbarLfsNotTracked,
                                    _ => string.Empty,
                                };
                            }),
                            Color = Prop.Bind(() => status.Value == LfsBadge.Tracked
                                ? theme.Styles.Value.DiffView.LfsBadgeTrackedText
                                : theme.Styles.Value.DiffView.LfsBadgeUntrackedText),
                        },
                    ],
                },
            ],
        };
    }
}
