using GitBench.Controls;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// Themed checkbox bound two-way to a <see cref="State{T}"/> — toggling the box writes the
/// state, external writes move the box. The binding's lifetime follows the view's mount.
/// </summary>
public sealed record Checkbox : Widget
{
    public required string Label { get; init; }
    public required State<bool> Value { get; init; }

    protected override View CreateView(Context ctx)
    {
        var view = new CheckboxView(ctx, Label);
        view.BindTwoWay(view.IsChecked, Value);
        return view;
    }
}
