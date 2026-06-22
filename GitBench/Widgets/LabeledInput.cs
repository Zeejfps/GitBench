using GitBench.Controls.Dialogs;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// Single-line dialog text field: a label above the box, an optional hint and validation message
/// below, two-way bound through a string <see cref="Prop{T}"/> (write-back active when its source is
/// writable). A non-null <see cref="Status"/> recolors the border and reveals the message line
/// (both keyed off <see cref="FieldSeverity"/>); a null status is neutral and collapses the message
/// line so the dialog reflows tightly. Inside a <see cref="Dialog"/> body it auto-registers for
/// Enter-submit/Esc-cancel and first-field focus.
/// </summary>
internal sealed record LabeledInput : Widget
{
    // Tall enough that the input's content area (BoxHeight - border - padding) is at least the
    // font's full line height; otherwise descenders (g, j, p, y) get scissored by the box clip.
    private const float BoxHeight = 32f;

    public required string Label { get; init; }
    public required Prop<string> Value { get; init; }
    public string? Placeholder { get; init; }
    public string? Hint { get; init; }

    /// <summary>Validation state; null is neutral. Drives border color and the message line.</summary>
    public IReadable<FieldStatus?>? Status { get; init; }

    /// <summary>Select the current text when the dialog opens (rename-style dialogs).</summary>
    public bool SelectAllOnOpen { get; init; }

    /// <summary>Trailing widget beside the box (e.g. a Browse… button).</summary>
    public IWidget? Accessory { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var styles = ctx.Theme().Styles;

        var input = DialogFrame.TextInput(ctx);
        if (Placeholder != null) input.PlaceholderText = Placeholder;
        input.BindTwoWay(Value.ToReadable(ctx), Value.Write);
        ctx.Get<DialogInputRegistry>()?.Register(input, SelectAllOnOpen);

        Prop<BorderColorStyle> borderColor = Status is { } statusBorder
            ? Prop.Bind(() => BorderColorStyle.All(statusBorder.Value?.Severity switch
            {
                FieldSeverity.Error => styles.Value.DialogFrame.ErrorText,
                FieldSeverity.Warning => styles.Value.DialogFrame.WarningText,
                _ => styles.Value.TextInput.Border,
            }))
            : Theme.BorderColor(s => BorderColorStyle.All(s.TextInput.Border));

        var box = new Box
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DialogFrame.ControlBorderRadius),
            Background = Theme.Color(s => s.TextInput.Background),
            BorderColor = borderColor,
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = 6, Right = 6, Top = 4, Bottom = 4 },
                    Children = [new Raw { View = input }],
                },
            ],
        };

        List<IWidget> boxRow = [new Grow { Child = box }];
        if (Accessory != null) boxRow.Add(Accessory);

        List<IWidget> children =
        [
            new Text
            {
                Value = Label,
                Color = Theme.Color(s => s.DialogBody.SectionHeaderText),
            },
            new Row
            {
                Gap = 8,
                CrossAxis = CrossAxisAlignment.Stretch,
                Height = BoxHeight,
                Children = [.. boxRow],
            },
        ];

        if (!string.IsNullOrEmpty(Hint))
        {
            children.Add(new Text
            {
                Value = Hint,
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(s => s.DialogBody.RowTextMissing),
            });
        }

        if (Status is { } status)
        {
            children.Add(new Text
            {
                Value = status.Bind(s => s?.Message),
                Wrap = TextWrap.Wrap,
                Visible = Prop.Bind(() => status.Value != null),
                Color = Prop.Bind(() => status.Value?.Severity == FieldSeverity.Warning
                    ? styles.Value.DialogFrame.WarningText
                    : styles.Value.DialogFrame.ErrorText),
            });
        }

        return new Column
        {
            Gap = 4,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children = [.. children],
        };
    }
}
