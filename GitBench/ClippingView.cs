using ZGF.Gui;

namespace GitGui;

// Container that clips its descendants to its own bounds. The framework's RectView
// doesn't clip — only ScrollPane variants do — so any child that measures wider than
// the parent draws past the edge. Wrap a dialog root in ClippingView to keep an
// over-wide row from escaping the dialog visually.
public sealed class ClippingView : MultiChildView
{
    public override bool ClipsContent => true;

    protected override void OnDrawChildren(ICanvas c)
    {
        c.PushClip(Position);
        base.OnDrawChildren(c);
        c.PopClip();
    }
}
