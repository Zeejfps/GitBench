using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

public sealed class DragOverlay : MultiChildView
{
    private readonly RectView _indicator;
    private IDragController? _dragController;
    private IDisposable? _subscription;

    public DragOverlay()
    {
        ZIndex = 900;
        _indicator = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(1),
        };
        _indicator.BindThemedBackgroundColor(s => s.Palette.Accent);
    }

    protected override void OnAttachedToContext(Context context)
    {
        base.OnAttachedToContext(context);
        _dragController = context.Get<IDragController>();
        if (_dragController is null) return;
        _subscription = _dragController.Target.Subscribe(OnTargetChanged);
    }

    protected override void OnDetachedFromContext(Context context)
    {
        _subscription?.Dispose();
        _subscription = null;
        _dragController = null;
        base.OnDetachedFromContext(context);
    }

    private void OnTargetChanged(DropTarget? target)
    {
        if (target is null)
        {
            if (Children.Contains(_indicator)) Children.Remove(_indicator);
            return;
        }

        if (!Children.Contains(_indicator)) Children.Add(_indicator);
        SetDirty();
    }

    protected override void OnLayoutChildren()
    {
        var target = _dragController?.Target.Value;
        if (target is null) return;

        var bounds = target.IndicatorBounds;
        _indicator.LeftConstraint = bounds.Left;
        _indicator.BottomConstraint = bounds.Bottom;
        _indicator.WidthConstraint = bounds.Width;
        _indicator.HeightConstraint = bounds.Height;
        _indicator.LayoutSelf();
    }
}
