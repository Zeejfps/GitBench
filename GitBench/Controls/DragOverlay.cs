using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// Top z-layer that paints a single drop-target indicator while a drag is in progress,
/// tracking <see cref="IDragController.Target"/>. Draws nothing when no drag is active.
/// </summary>
public sealed record DragOverlay : Widget
{
    protected override View CreateView(Context ctx) => new Core(ctx);

    private sealed class Core : ContainerView
    {
        private readonly RectView _indicator;
        private readonly IDragController? _dragController;

        public Core(Context ctx)
        {
            ZIndex = 900;
            _indicator = new RectView
            {
                BorderRadius = BorderRadiusStyle.All(1),
            };
            _indicator.BindThemedBackgroundColor(ctx.Theme(), s => s.Palette.Accent);

            _dragController = ctx.Get<IDragController>();
            if (_dragController is { } drag)
                this.Use(() => drag.Target.Subscribe(OnTargetChanged));
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
}
