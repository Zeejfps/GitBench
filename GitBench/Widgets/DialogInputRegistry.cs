using ZGF.Gui.Desktop.Components.TextInput;

namespace GitBench.Widgets;

/// <summary>
/// Build-time collaboration channel between <see cref="Dialog"/> and the input widgets in its
/// body. The dialog registers one of these on a child build scope; <see cref="LabeledInput"/>
/// (and future input widgets) resolve it and register their inner <see cref="TextInputView"/>.
/// After the body is built, the dialog wires Enter-submits/Esc-cancels across the registered
/// inputs and focuses the first one on mount — the widget-land equivalent of
/// <c>DialogShell.SubmitFrom</c> + <c>BeginEditing</c>.
/// </summary>
internal sealed class DialogInputRegistry
{
    public readonly record struct Entry(TextInputView Input, bool SelectAllOnOpen);

    public List<Entry> Entries { get; } = new();

    public void Register(TextInputView input, bool selectAllOnOpen) =>
        Entries.Add(new Entry(input, selectAllOnOpen));
}
