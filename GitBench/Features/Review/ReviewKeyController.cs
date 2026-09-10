using GitBench.Input;
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
/// <item><see cref="KeyCommand.ReviewNextFile"/> / <see cref="KeyCommand.ReviewPrevFile"/> — step through the range.</item>
/// <item><see cref="KeyCommand.ReviewToggleMark"/> — toggle the selected file's mark.</item>
/// <item><see cref="KeyCommand.ReviewToggleHelp"/> — show / hide the keyboard cheatsheet; <c>Esc</c> dismisses it.</item>
/// </list>
/// It never steals focus, so the file list keeps the focus it needs for its own Up/Down arrows. While
/// the cheatsheet is open the loop keys are swallowed so they don't drive the surface behind it.
/// </summary>
internal sealed class ReviewKeyController : KeyboardMouseController
{
    private readonly IReviewSurfaceModel _vm;
    private readonly IKeyMap _keys;

    public ReviewKeyController(IReviewSurfaceModel vm, IKeyMap keys)
    {
        _vm = vm;
        _keys = keys;
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;

        // Toggles the cheatsheet from any state.
        if (_keys.Matches(KeyCommand.ReviewToggleHelp, e.Key, e.Modifiers))
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

        if (_keys.Matches(KeyCommand.ReviewNextFile, e.Key, e.Modifiers))
        {
            _vm.NextFile();
            e.Consume();
        }
        else if (_keys.Matches(KeyCommand.ReviewPrevFile, e.Key, e.Modifiers))
        {
            _vm.PrevFile();
            e.Consume();
        }
        else if (_keys.Matches(KeyCommand.ReviewToggleMark, e.Key, e.Modifiers))
        {
            _vm.ToggleActiveFileViewed();
            e.Consume();
        }
    }
}
