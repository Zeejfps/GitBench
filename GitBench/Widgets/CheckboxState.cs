using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// A <see cref="CheckboxWidget"/>'s live state: the controller-driven interaction trio plus the bound
/// <see cref="Checked"/> value, and the toggle behavior that writes the rising edge of
/// <see cref="IInteractable.Pressed"/> back through the checked source. The widget delegates its
/// <see cref="IInteractable"/>/<see cref="ICheckbox"/> surface to this, so the controller drives it
/// and the theme reads it from one place. Plain leaf states (nothing external subscribes to them),
/// so they need no disposal — they fall out of scope with the view tree.
/// </summary>
public sealed class CheckboxState : ICheckbox
{
    private readonly State<bool> _hovered = new(false);
    private readonly State<bool> _pressed = new(false);
    private readonly State<bool> _enabled = new(true);

    public IWritable<bool> Hovered => _hovered;
    public IWritable<bool> Pressed => _pressed;
    public IReadable<bool> Enabled => _enabled;
    public IReadable<bool> Checked { get; }

    /// <param name="checked">The resolved read side of the widget's <c>Checked</c> prop.</param>
    /// <param name="writeChecked">Write-back into that prop; a no-op when the source isn't writable.</param>
    public CheckboxState(IReadable<bool> @checked, Action<bool> writeChecked)
    {
        Checked = @checked;
        _pressed.Changed += pressed =>
        {
            if (pressed) writeChecked(!@checked.Value);
        };
    }
}
