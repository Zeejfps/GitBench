using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// An <see cref="ActionButton"/>'s surface to its theme styling: an <see cref="IInteractable"/>
/// (hover/press/enabled) plus the optional solid <see cref="Fill"/>, letting <c>ActionButtonStyles</c>
/// resolve every color from one reference instead of loose state and a fill parameter. Implemented by
/// <see cref="ActionButtonState"/>.
/// </summary>
internal interface IActionButton : IInteractable
{
    /// <summary>The solid fill when the button is a filled chip; null for the plain themed treatment.</summary>
    IReadable<uint?> Fill { get; }
}
