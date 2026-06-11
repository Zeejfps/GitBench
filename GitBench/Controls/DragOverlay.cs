using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Views;

namespace GitBench.Controls;

public sealed class DragOverlay : ContainerView
{
    private readonly RectView _indicator;
    private IDragController? _dragController;

    public DragOverlay()
    {
        ZIndex = 900;
        _indicator = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(1),
        };
        _indicator.BindThemedBackgroundColor(s => s.Palette.Accent);

        this.Use(ctx =>
        {
            _dragController = ctx.Get<IDragController>();
            var subscription = _dragController?.Target.Subscribe(OnTargetChanged);
            return new ActionDisposable(() =>
            {
                subscription?.Dispose();
                _dragController = null;
            });
        });
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
