using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Branches;

// Clears the branch selection when a left-click lands on the list background — a click that no row
// consumed bubbles up to here. Rows consume their own clicks, so this fires only on empty space,
// reproducing the old list's "click below the rows clears the selection". The press arms; a release
// without one (e.g. the tail of a menu-dismiss click) does nothing.
internal sealed class BranchBackgroundController(BranchesViewModel vm) : KeyboardMouseController
{
    private bool _armed;

    public override void OnMouseExit(ref MouseExitEvent e) => _armed = false;

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button != MouseButton.Left) return;

        if (e.State == InputState.Pressed)
        {
            _armed = true;
            e.Consume();
            return;
        }

        if (e.State != InputState.Released || !_armed) return;
        _armed = false;
        vm.ClearSelection();
        e.Consume();
    }
}
