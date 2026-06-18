using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// The state surface a <see cref="Checkbox"/> exposes to its theme styling: the
/// <see cref="IInteractable"/> interaction states plus the two-way <see cref="Checked"/> value, so
/// <c>CheckboxStyles</c> resolves every color from one reference instead of loose booleans.
/// </summary>
public interface ICheckbox : IInteractable
{
    /// <summary>True when the box is ticked; the toggle target a click writes.</summary>
    State<bool> Checked { get; }
}
