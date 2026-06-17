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
    private const float SeparatorBreathingRoom = 9f;
    private const float SeparatorHeight = 18f;

    protected override IWidget Build(Context ctx)
    {
        return new Center
        {
            Width = SeparatorWidth + SeparatorBreathingRoom * 2,
            Child = new Box
            {
                Width = SeparatorWidth,
                Height = SeparatorHeight,
                Background = Theme.Color(s => s.Palette.Border),
            },
        };
    }
}
