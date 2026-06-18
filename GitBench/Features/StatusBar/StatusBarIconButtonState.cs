using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

/// <summary>
/// A <see cref="StatusBarIconButton"/>'s live state: the controller-driven hover/press/enabled trio
/// with activation fused in — the rising edge of <see cref="IInteractable.Pressed"/> runs the bound
/// <see cref="ICommand"/>, and <see cref="IInteractable.Enabled"/> tracks its
/// <see cref="ICommand.CanExecute"/>. The button exposes it as its <see cref="IInteractable"/> surface,
/// so the controller drives it and the theme reads hover/enabled from one place. A plain leaf state
/// (nothing external subscribes), so it needs no disposal — it falls out of scope with the view tree.
/// </summary>
internal sealed class StatusBarIconButtonState : IInteractable
{
    private readonly State<bool> _hovered = new(false);
    private readonly State<bool> _pressed = new(false);

    public IWritable<bool> Hovered => _hovered;
    public IWritable<bool> Pressed => _pressed;
    public IReadable<bool> Enabled { get; }

    /// <param name="command">The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</param>
    public StatusBarIconButtonState(ICommand command)
    {
        Enabled = command.CanExecute;
        _pressed.Changed += pressed =>
        {
            if (pressed) command.Execute();
        };
    }
}
