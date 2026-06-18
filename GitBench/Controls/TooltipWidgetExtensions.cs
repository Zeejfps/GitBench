using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

public static class TooltipWidgetExtensions
{
    /// <summary>
    /// Attaches a hover <see cref="Tooltip"/> to the built view, or no-ops when <paramref name="text"/>
    /// is unset — so a widget can end its Build with this unconditionally instead of branching on
    /// whether a tooltip was supplied.
    /// </summary>
    public static IWidget WithTooltip(this IWidget widget, Context ctx, Prop<string?> text,
        IReadable<bool> hovered, IReadable<bool> enabled) =>
        !text.IsSet
            ? widget
            : widget.Use(v => new Tooltip(v, ctx, text.ToReadable(ctx), hovered, enabled));
}
