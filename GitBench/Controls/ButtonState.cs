using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// Shared live state for a command-gated pressable: the controller-driven hover/press/enabled trio with
/// activation fused in — the rising edge of <see cref="IInteractable.Pressed"/> runs the bound
/// <see cref="ICommand"/>, and <see cref="IInteractable.Enabled"/> tracks its
/// <see cref="ICommand.CanExecute"/>. A widget owns it and exposes it as its <see cref="IInteractable"/>
/// surface, so a controller drives it and the theme reads hover/enabled from one place. Backs
/// <see cref="ButtonWidget"/> (in all its styles) and any other widget whose interaction is "a press
/// runs a command". A plain leaf state (nothing external subscribes), so it needs no disposal — it
/// falls out of scope with the view tree.
/// </summary>
internal sealed class ButtonState : IInteractable
{
    private static readonly State<bool> AlwaysEnabled = new(true);

    private readonly State<bool> _hovered = new(false);
    private readonly State<bool> _pressed = new(false);

    public IWritable<bool> Hovered => _hovered;
    public IWritable<bool> Pressed => _pressed;
    public IReadable<bool> Enabled { get; }

    /// <param name="command">The action a press runs; its <see cref="ICommand.CanExecute"/> gates the
    /// button. Null leaves the button inert on press.</param>
    /// <param name="enabled">Explicit enabled source — for a button with no command but a gated state
    /// (e.g. a dropdown disabled when it has nothing to pick). Falls back to the command's
    /// <see cref="ICommand.CanExecute"/>, then to always-enabled.</param>
    public ButtonState(ICommand? command = null, IReadable<bool>? enabled = null)
    {
        Enabled = enabled ?? command?.CanExecute ?? AlwaysEnabled;
        _pressed.Changed += pressed =>
        {
            if (pressed) command?.Execute();
        };
    }
}
