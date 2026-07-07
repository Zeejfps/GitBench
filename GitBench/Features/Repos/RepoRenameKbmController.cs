using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Features.Repos;

internal sealed class RepoRenameKbmController : BaseTextInputKbmController
{
    private readonly TextInputView _input;
    private readonly Guid _repoId;
    private readonly IRepoRegistry _registry;
    private bool _finished;

    public RepoRenameKbmController(TextInputView input, InputSystem inputSystem, ZGF.Gui.IClipboard? clipboard, Guid repoId, IRepoRegistry registry) : base(input, inputSystem, clipboard)
    {
        _input = input;
        _repoId = repoId;
        _registry = registry;
        _input.StartEditing();
        inputSystem.StealFocus(this);
    }

    protected override void OnKeyboardKeyPressed(ref KeyboardKeyEvent e)
    {
        if (_finished) return;

        if (e.Key == KeyboardKey.Enter || e.Key == KeyboardKey.NumpadEnter)
        {
            e.Consume();
            Commit();
            return;
        }
        if (e.Key == KeyboardKey.Escape)
        {
            e.Consume();
            Cancel();
            return;
        }
        base.OnKeyboardKeyPressed(ref e);
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (_finished) return;

        if (e.Phase == EventPhase.Bubbling
            && e.State == InputState.Pressed
            && e.Button == MouseButton.Left
            && _input.IsEditing
            && !_input.Position.ContainsPoint(e.Mouse.Point))
        {
            Commit();
            return;
        }
        base.OnMouseButtonStateChanged(ref e);
    }

    public override void OnFocusLost()
    {
        if (_finished) return;
        Commit();
    }

    private void Commit()
    {
        if (_finished) return;
        _finished = true;
        var newName = new string(_input.Text);
        _input.StopEditing();
        _registry.RenameRepo(_repoId, newName);
        _registry.EndRenameRepo();
    }

    private void Cancel()
    {
        if (_finished) return;
        _finished = true;
        _input.StopEditing();
        _registry.EndRenameRepo();
    }
}
