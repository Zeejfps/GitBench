using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.KeyboardModule;

namespace GitGui;

internal sealed class GroupRenameKbmController : BaseTextInputKbmController
{
    private readonly TextInputView _input;
    private readonly Guid _groupId;
    private readonly IRepoRegistry _registry;
    private bool _finished;

    public GroupRenameKbmController(TextInputView input, InputSystem inputSystem, Guid groupId, IRepoRegistry registry) : base(input)
    {
        _input = input;
        _groupId = groupId;
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
        _registry.RenameGroup(_groupId, newName);
        _registry.EndRenameGroup();
    }

    private void Cancel()
    {
        if (_finished) return;
        _finished = true;
        _input.StopEditing();
        _registry.EndRenameGroup();
    }
}