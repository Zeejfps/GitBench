using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// The text label segment of an <see cref="ButtonWidget"/>. Its color tracks the ambient
/// <see cref="Foreground"/> (the idle/hover/disabled ramp the button establishes), so it renders
/// correctly inside any <see cref="Foreground"/> scope.
/// </summary>
internal sealed record ButtonLabel : Widget
{
    public required Prop<string?> Value { get; init; }

    protected override IWidget Build(Context ctx) =>
        new Text { Value = Value, VAlign = TextAlignment.Center, Color = Foreground.Color };
}
