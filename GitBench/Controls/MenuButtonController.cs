using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// Drives a menu-button's hover state and, on left-click, opens a menu anchored at the button's own
/// rect. Attach it via the view-aware <c>WithController(input, v =&gt; …)</c> overload so it receives
/// the built view — the rect the menu anchors to isn't known until the view is laid out, and the
/// click event carries only the pointer position, not the view.
/// </summary>
public sealed class MenuButtonController : KeyboardMouseController
{
    private readonly View _view;
    private readonly IInteractable _state;
    private readonly Action<PointF> _openMenu;

    public MenuButtonController(View view, IInteractable state, Action<PointF> openMenu)
    {
        _view = view;
        _state = state;
        _openMenu = openMenu;
    }

    public override void OnMouseEnter(ref MouseEnterEvent e) => _state.Hovered.Value = true;

    public override void OnMouseExit(ref MouseExitEvent e) => _state.Hovered.Value = false;

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button != MouseButton.Left) return;
        if (e.State != InputState.Pressed) return;

        _openMenu(_view.Position.TopLeft);
        e.Consume();
    }
}
