using GitBench.Controls;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Widgets;

/// <summary>Clips its child to the allotted bounds (e.g. long ref names in fixed rows).</summary>
public sealed record Clipped : Widget
{
    public required IWidget Child { get; init; }

    protected override View CreateView(Context ctx) =>
        new ClippingView { Children = { Child.BuildView(ctx) } };
}
