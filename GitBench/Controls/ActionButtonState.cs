using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// An <see cref="ActionButton"/>'s live state: the controller-driven hover/press/enabled trio plus the
/// resolved <see cref="Fill"/>, with activation fused in — the rising edge of
/// <see cref="IInteractable.Pressed"/> runs the bound <see cref="ICommand"/>, and
/// <see cref="IInteractable.Enabled"/> tracks its <see cref="ICommand.CanExecute"/>. The widget delegates
/// its <see cref="IInteractable"/>/<see cref="IActionButton"/> surface to this, so the controller drives
/// it and <c>ActionButtonStyles</c> resolves every color from one reference. A plain leaf state (nothing
/// external subscribes), so it needs no disposal — it falls out of scope with the view tree.
/// </summary>
internal sealed class ActionButtonState : IActionButton
{
    private static readonly State<bool> AlwaysEnabled = new(true);

    private readonly State<bool> _hovered = new(false);
    private readonly State<bool> _pressed = new(false);

    public IWritable<bool> Hovered => _hovered;
    public IWritable<bool> Pressed => _pressed;
    public IReadable<bool> Enabled { get; }
    public IReadable<uint?> Fill { get; }

    /// <param name="command">The action a press runs; its <see cref="ICommand.CanExecute"/> gates the
    /// button. Null leaves the button always-enabled and inert on press.</param>
    /// <param name="fill">The resolved read side of the widget's <c>Background</c> prop; null renders the
    /// plain themed button.</param>
    public ActionButtonState(ICommand? command, IReadable<uint?> fill)
    {
        Enabled = command?.CanExecute ?? AlwaysEnabled;
        Fill = fill;
        _pressed.Changed += pressed =>
        {
            if (pressed) command?.Execute();
        };
    }
}
