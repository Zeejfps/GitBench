using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// The outline "select"/secondary-button chrome: a 1px bordered, rounded surface whose background and
/// border follow the bordered-button idle/hover/disabled ramp driven by the supplied interaction
/// <see cref="State"/>. It owns no state and no behavior — the parent passes in its button state and the
/// content; inner padding and sizing stay with the caller.
/// </summary>
internal sealed record BorderedButtonSurface : Widget
{
    /// <summary>Interaction state driving the idle/hover/disabled ramp.</summary>
    public required IInteractable State { get; init; }

    /// <summary>Corner radius of the rounded outline.</summary>
    public float Radius { get; init; } = 6f;

    /// <summary>Content placed inside the surface.</summary>
    public required IWidget[] Children { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        BorderSize = BorderSizeStyle.All(1),
        BorderRadius = BorderRadiusStyle.All(Radius),
        Background = Theme.Color(s => s.BorderedButton.Surface(State)),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.BorderedButton.Border(State))),
        Children = Children,
    };
}
