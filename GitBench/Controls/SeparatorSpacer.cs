using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// Short vertical hairline with breathing room on either side — used to mark zone
/// boundaries inside a button toolbar. The contrast between tight intra-cluster gaps and
/// this wider separator block is what creates the visual grouping; uniform gaps would
/// make the toolbar read as one long row regardless of how many separators we drew.
/// </summary>
internal sealed record SeparatorSpacer : Widget
{
    private const float SeparatorWidth = 1f;
    private const int SeparatorBreathingRoom = 9;
    private const float SeparatorHeight = 18f;

    protected override IWidget Build(Context ctx)
    {
        // The hairline is vertically centered by the toolbar Row's CrossAxis=Center; wrapping it in
        // Padding (not Center) is deliberate — Center reserves a modal-sized margin that would clamp
        // this tiny box to zero.
        return new Padding
        {
            Amount = new PaddingStyle { Left = SeparatorBreathingRoom, Right = SeparatorBreathingRoom },
            Children =
            [
                new Box
                {
                    Width = SeparatorWidth,
                    Height = SeparatorHeight,
                    Background = Theme.Color(s => s.Palette.Border),
                },
            ],
        };
    }
}
