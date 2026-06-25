using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Branches;

// Clears the branch selection when a left-click lands on the list background — a click that no row
// consumed bubbles up to here. Rows consume their own clicks, so this fires only on empty space,
// reproducing the old list's "click below the rows clears the selection".
internal sealed class BranchBackgroundController(BranchesViewModel vm) : KeyboardMouseController
{
    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button != MouseButton.Left || e.State != InputState.Released) return;
        vm.ClearSelection();
        e.Consume();
    }
}
