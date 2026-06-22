using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// A labeled dialog button over the shared <see cref="ButtonState"/> primitive, in one of the
/// <see cref="DialogButtonRole"/> looks, with an optional leading icon (e.g. a busy spinner). Live
/// state is exposed as the widget's <see cref="IInteractable"/> surface, so the parent attaches a
/// controller (<c>button.WithController&lt;KbmController&gt;()</c>) and a press runs <see cref="Command"/>.
/// Size it with the inherited <c>Height</c>/<c>MinWidth</c>.
/// </summary>
internal sealed record DialogButtonWidget : Widget<ButtonState>
{
    /// <summary>The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</summary>
    public ICommand? Command { get; init; }

    public required Prop<string?> Label { get; init; }

    public DialogButtonRole Role { get; init; } = DialogButtonRole.Default;

    /// <summary>Optional leading glyph; hidden when null/empty so the label stays centered.</summary>
    public Prop<string?> Icon { get; init; }

    /// <summary>Leading glyph angle (radians); drive from a spinner animation while an op runs.</summary>
    public Prop<float> IconRotation { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(Command);

    protected override IWidget Build(Context ctx, ButtonState state)
    {
        var destructive = Role == DialogButtonRole.Destructive;

        Prop<uint> surface = Role == DialogButtonRole.Default
            ? Theme.Color(s => s.BorderedButton.Surface(state))
            : Theme.Color(s => s.DialogActionButton.Fill(destructive, state));
        Prop<BorderColorStyle> border = Role == DialogButtonRole.Default
            ? Theme.BorderColor(s => BorderColorStyle.All(s.BorderedButton.Border(state)))
            : Theme.BorderColor(s => BorderColorStyle.All(s.DialogActionButton.Fill(destructive, state)));
        Prop<uint> foreground = Role == DialogButtonRole.Default
            ? Theme.Color(s => s.BorderedButton.Foreground(state))
            : Theme.Color(s => s.DialogActionButton.Foreground(destructive, state));

        var label = new Text
        {
            Value = Label,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Foreground.Color,
        };

        // The icon rides ahead of the label only when one is set; an unset/empty glyph collapses
        // (no row gap) so a label-only button stays centered.
        IWidget[] content = Icon.IsSet
            ?
            [
                new Text
                {
                    FontFamily = LucideIcons.FontFamily,
                    FontSize = 14,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Value = Icon,
                    Rotation = IconRotation,
                    Color = Foreground.Color,
                    Visible = Icon.Select(i => !string.IsNullOrEmpty(i)),
                },
                label,
            ]
            : [label];

        return new Box
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DialogFrame.ControlBorderRadius),
            Background = surface,
            BorderColor = border,
            Children =
            [
                new Padding
                {
                    // Horizontal padding gives short labels breathing room and lets the button size
                    // to its text (clamped up by MinWidth in DialogFrame.ButtonsRow).
                    Amount = new PaddingStyle { Left = 16, Right = 16 },
                    Children =
                    [
                        new Foreground
                        {
                            Value = foreground,
                            Child = new Row
                            {
                                Gap = 6,
                                MainAxis = MainAxisAlignment.Center,
                                CrossAxis = CrossAxisAlignment.Center,
                                Children = content,
                            },
                        },
                    ],
                },
            ],
        };
    }
}
