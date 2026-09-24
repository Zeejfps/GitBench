using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Widgets;

/// <summary>
/// Draws its child only when given at least the child's natural width, so a squeezed label disappears
/// whole instead of rendering clipped or as a stray ellipsis. Pair with <see cref="Shrink"/> to let it
/// give up its space first.
/// </summary>
public sealed record FitOrHide : Widget
{
    public required IWidget Child { get; init; }

    protected override View CreateView(Context ctx) => new Core(Child.BuildView(ctx));

    private sealed class Core : View
    {
        private readonly View _child;

        public Core(View child)
        {
            _child = child;
            AddChildToSelf(child);
        }

        protected override void OnDrawChildren(ICanvas c)
        {
            if (Position.Width + 0.5f >= _child.MeasureWidth())
                base.OnDrawChildren(c);
        }
    }
}
