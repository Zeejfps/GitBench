using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// The text label segment of an <see cref="ActionButton"/>. Its color tracks the button's themed
/// foreground (the idle/hover/disabled ramp) via <see cref="ActionButton.Foreground"/>, so it only
/// renders correctly inside an <see cref="ActionButton"/>'s content.
/// </summary>
internal sealed record ButtonLabel : Widget
{
    public required Prop<string?> Value { get; init; }

    protected override IWidget Build(Context ctx) =>
        new Text { Value = Value, VAlign = TextAlignment.Center, Color = ActionButton.Foreground };
}
