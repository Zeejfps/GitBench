using ZGF.Gui;

namespace GitGui;

/// <summary>
/// Three-column row for the local-changes layout: left panel | fixed-width center |
/// right panel. The two side panels are guaranteed equal width — the center's measured
/// width is subtracted from the row's width and the remainder is split exactly in half.
/// </summary>
internal sealed class TransferListRow : MultiChildView
{
    private readonly View _left;
    private readonly View _center;
    private readonly View _right;

    public TransferListRow(View left, View center, View right)
    {
        _left = left;
        _center = center;
        _right = right;
        AddChildToSelf(left);
        AddChildToSelf(center);
        AddChildToSelf(right);
    }

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        if (pos.Width <= 0f) return;

        var centerWidth = Math.Min(_center.MeasureWidth(), pos.Width);
        var sideWidth = Math.Max(0f, (pos.Width - centerWidth) / 2f);
        // Re-derive in case rounding pushed sideWidth lopsided.
        centerWidth = pos.Width - sideWidth * 2f;

        LayoutChild(_left, pos.Left, sideWidth, pos);
        LayoutChild(_center, pos.Left + sideWidth, centerWidth, pos);
        LayoutChild(_right, pos.Left + sideWidth + centerWidth, sideWidth, pos);
    }

    private static void LayoutChild(View child, float left, float width, in ZGF.Geometry.RectF parent)
    {
        child.LeftConstraint = left;
        child.BottomConstraint = parent.Bottom;
        child.WidthConstraint = width;
        child.HeightConstraint = parent.Height;
        child.LayoutSelf();
    }
}