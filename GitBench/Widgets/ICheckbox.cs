using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// A checkbox's surface to its theme styling: an <see cref="IInteractable"/> (hover/press/enabled)
/// plus the two-way <see cref="Checked"/> value, letting <c>CheckboxStyles</c> resolve every color
/// from one reference instead of loose booleans. Implemented by <see cref="CheckboxState"/>.
/// </summary>
public interface ICheckbox : IInteractable
{
    /// <summary>True when the box is ticked; the read side of the value a click writes back.</summary>
    IReadable<bool> Checked { get; }
}
