using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// Turns any pressable into a menu button: drives an <see cref="IInteractable"/>'s hover and, on
/// left-click, runs <c>onOpen</c> anchored at the control's own rect. The menu is supplied by the
/// caller, not baked into the widget — so <c>anyButton.WithMenuController(rect =&gt; …)</c> bolts a menu
/// onto whatever look the widget provides.
/// </summary>
internal sealed class MenuButtonController : KeyboardMouseController
{
    private readonly IInteractable _target;
    private readonly View _view;
    private readonly Action<RectF> _onOpen;

    public MenuButtonController(IInteractable target, View view, Action<RectF> onOpen)
    {
        _target = target;
        _view = view;
        _onOpen = onOpen;
    }

    public override void OnMouseEnter(ref MouseEnterEvent e)
    {
        if (!_target.Enabled.Value) return;
        _target.Hovered.Value = true;
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        _target.Hovered.Value = false;
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (!_target.Enabled.Value) return;
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button != MouseButton.Left) return;
        if (e.State != InputState.Pressed) return;

        _onOpen(_view.Position);
        e.Consume();
    }
}

internal static class MenuButtonControllerExtensions
{
    /// <summary>
    /// Attaches a <see cref="MenuButtonController"/> to a pressable widget, driving its
    /// <see cref="IInteractable"/> state and opening <paramref name="onOpen"/> at the widget's rect on
    /// click — the menu-button counterpart of <c>checkbox.WithController&lt;KbmController&gt;()</c>.
    /// </summary>
    public static IWidget WithMenuController(this IWidget<IInteractable> widget, Action<RectF> onOpen) =>
        widget.WithController((ctx, v) => new MenuButtonController(widget.State, v, onOpen));
}
