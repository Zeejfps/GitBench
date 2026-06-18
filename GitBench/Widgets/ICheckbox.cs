using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// A <see cref="Checkbox"/>'s surface to its theme styling: an <see cref="IInteractableWidget"/> (so a
/// controller can drive its hover/press) plus the two-way <see cref="Checked"/> value, letting
/// <c>CheckboxStyles</c> resolve every color from one reference instead of loose booleans.
/// </summary>
public interface ICheckbox : IInteractableWidget
{
    /// <summary>True when the box is ticked; the toggle target a click writes.</summary>
    State<bool> Checked { get; }
}
