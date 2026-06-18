using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

public static class TooltipWidgetExtensions
{
    private static readonly IReadable<bool> AlwaysEnabled = new State<bool>(true);

    /// <summary>
    /// Adds a hover <see cref="Tooltip"/> to an interactive widget — the parent decides whether a
    /// button gets a tooltip by calling this, rather than the button carrying a tooltip field. Reads
    /// hover from the widget's <see cref="IInteractable"/> state and takes its <see cref="Context"/>
    /// from the build itself, so neither is passed in. The tooltip shows on hover regardless of the
    /// widget's enabled state — a disabled control still explains what it does. Returns the same
    /// <see cref="IWidget{TState}"/> so it chains ahead of <c>WithController</c>. No-ops when
    /// <paramref name="text"/> is unset.
    /// </summary>
    public static IWidget<TState> WithTooltip<TState>(this IWidget<TState> widget, Prop<string?> text)
        where TState : class, IInteractable =>
        !text.IsSet ? widget : new TooltipAttachment<TState>(widget, text);

    private sealed record TooltipAttachment<TState>(IWidget<TState> Child, Prop<string?> Text)
        : IWidget<TState> where TState : class, IInteractable
    {
        public TState State => Child.State;

        public View BuildView(Context ctx)
        {
            var view = Child.BuildView(ctx);
            view.Use(() => new Tooltip(view, ctx, Text.ToReadable(ctx), State.Hovered, AlwaysEnabled));
            return view;
        }
    }
}
