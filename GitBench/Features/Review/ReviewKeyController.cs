using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Features.Review;

/// <summary>
/// Window-level keyboard for the review loop, attached to the review window's root so it sits in the
/// hover/bubble dispatch path for the whole window. Handles the loop-critical keys on bubbling — after
/// the focused file list (which only consumes its own arrow/enter keys), so navigation there is
/// untouched:
/// <list type="bullet">
/// <item><c>[</c> / <c>]</c> — previous / next increment.</item>
/// <item><c>j</c> / <c>k</c> — next / previous file in the increment.</item>
/// <item><c>Space</c> / <c>Enter</c> — run the primary action (mark viewed → advance, or next increment).</item>
/// </list>
/// It never steals focus, so the file list keeps the focus it needs for its own Up/Down arrows. The
/// remaining keys (<c>v</c>, <c>n</c>) and the cheatsheet land in Phase 6.
/// </summary>
internal sealed class ReviewKeyController : KeyboardMouseController
{
    private readonly ReviewWindowViewModel _vm;

    public ReviewKeyController(ReviewWindowViewModel vm) => _vm = vm;

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;

        switch (e.Key)
        {
            case KeyboardKey.RightBracket:
                _vm.SelectNextIncrement();
                e.Consume();
                break;
            case KeyboardKey.LeftBracket:
                _vm.SelectPrevIncrement();
                e.Consume();
                break;
            case KeyboardKey.J:
                _vm.NextFile();
                e.Consume();
                break;
            case KeyboardKey.K:
                _vm.PrevFile();
                e.Consume();
                break;
            case KeyboardKey.Space:
            case KeyboardKey.Enter:
            case KeyboardKey.NumpadEnter:
                _vm.RunPrimaryAction();
                e.Consume();
                break;
        }
    }
}
