using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Features.Review;

/// <summary>
/// Window-level keyboard for the review loop, attached to the review window's root so it sits in the
/// hover/bubble dispatch path for the whole window. Handles the keys on bubbling — after the focused
/// file list (which only consumes its own arrow/enter keys), so navigation there is untouched:
/// <list type="bullet">
/// <item><c>j</c> / <c>k</c> — next / previous file in the range.</item>
/// <item><c>v</c> / <c>Space</c> / <c>Enter</c> — toggle the selected file's mark.</item>
/// <item><c>?</c> — show / hide the keyboard cheatsheet; <c>Esc</c> dismisses it.</item>
/// </list>
/// It never steals focus, so the file list keeps the focus it needs for its own Up/Down arrows. While
/// the cheatsheet is open the loop keys are swallowed so they don't drive the surface behind it.
/// </summary>
internal sealed class ReviewKeyController : KeyboardMouseController
{
    private readonly IReviewSurfaceModel _vm;

    public ReviewKeyController(IReviewSurfaceModel vm) => _vm = vm;

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;

        // '?' (Shift+/) toggles the cheatsheet from any state.
        if (e.Key == KeyboardKey.Slash && e.Modifiers.HasFlag(InputModifiers.Shift))
        {
            _vm.ToggleCheatsheet();
            e.Consume();
            return;
        }

        // While the help overlay is up, swallow the loop keys; Esc dismisses it.
        if (_vm.CheatsheetOpen.Value)
        {
            if (e.Key == KeyboardKey.Escape) _vm.CloseCheatsheet();
            e.Consume();
            return;
        }

        switch (e.Key)
        {
            case KeyboardKey.J:
                _vm.NextFile();
                e.Consume();
                break;
            case KeyboardKey.K:
                _vm.PrevFile();
                e.Consume();
                break;
            case KeyboardKey.V:
            case KeyboardKey.Space:
            case KeyboardKey.Enter:
            case KeyboardKey.NumpadEnter:
                _vm.ToggleActiveFileViewed();
                e.Consume();
                break;
        }
    }
}
