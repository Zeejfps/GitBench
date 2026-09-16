using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Settings;

/// <summary>A Settings row's single-line text box, sized to sit level with a
/// <see cref="SettingsDropdown"/> beside it. A true <see cref="Invalid"/> turns the border to the
/// error color.</summary>
internal sealed record SettingsTextField : Widget
{
    public required string FieldId { get; init; }
    public required Prop<string> Value { get; init; }
    public Prop<string?> Placeholder { get; init; }
    public IReadable<bool>? Invalid { get; init; }

    /// <summary>Draws the value as bullets — for a key, which is a secret on screen as much as at rest.</summary>
    public bool Masked { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var styles = ctx.Theme().Styles;

        Prop<BorderColorStyle> borderColor = Invalid is { } invalid
            ? Prop.Bind(() => BorderColorStyle.All(invalid.Value
                ? styles.Value.DialogFrame.ErrorText
                : styles.Value.TextInput.Border))
            : Theme.BorderColor(s => BorderColorStyle.All(s.TextInput.Border));

        return new Box
        {
            Width = Width,
            Height = Sizes.ControlHeight,
            Background = Theme.Color(s => s.TextInput.Background),
            BorderColor = borderColor,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Xs, Bottom = Spacing.Xs },
                    Children =
                    [
                        new TextInput
                        {
                            Id = FieldId,
                            Value = Value,
                            Masked = Masked,
                            Placeholder = Placeholder,
                            Wrap = TextWrap.NoWrap,
                            FontSize = FontSize.Body,
                            VAlign = TextAlignment.Center,
                            Background = Theme.Color(s => s.TextInput.Background),
                            Color = Theme.Color(s => s.TextInput.Text),
                            CaretColor = Theme.Color(s => s.TextInput.Caret),
                            SelectionColor = Theme.Color(s => s.TextInput.Selection),
                            PlaceholderColor = Theme.Color(s => s.TextInput.PlaceholderText),
                        },
                    ],
                },
            ],
        };
    }
}
