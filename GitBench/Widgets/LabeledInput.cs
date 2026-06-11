using GitBench.Controls;
using GitBench.Controls.Dialogs;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// Single-line dialog text field: label above, optional hint and validation message below,
/// two-way bound to a string <see cref="State{T}"/>. Inside a <see cref="Dialog"/> body it
/// auto-registers for Enter-submit/Esc-cancel and first-field focus.
/// </summary>
internal sealed record LabeledInput : Widget
{
    public required string Label { get; init; }
    public required State<string> Value { get; init; }
    public string? Placeholder { get; init; }
    public string? Hint { get; init; }

    /// <summary>Validation state; null is neutral. Drives border color and the message line.</summary>
    public IReadable<FieldStatus?>? Status { get; init; }

    /// <summary>Select the current text when the dialog opens (rename-style dialogs).</summary>
    public bool SelectAllOnOpen { get; init; }

    /// <summary>Trailing view beside the box (e.g. a Browse… button).</summary>
    public IWidget? Accessory { get; init; }

    protected override View CreateView(Context ctx)
    {
        var field = new LabeledInputField(Label);
        if (Placeholder != null) field.Placeholder = Placeholder;
        if (Hint != null) field.Hint = Hint;
        if (Status != null) field.BindStatus(Status);
        if (Accessory != null) field.Accessory = Accessory.BuildView(ctx);
        field.Input.BindTwoWay(Value);
        ctx.Get<DialogInputRegistry>()?.Register(field.Input, SelectAllOnOpen);
        return field;
    }
}
