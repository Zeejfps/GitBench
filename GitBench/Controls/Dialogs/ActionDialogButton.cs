using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// Emphasis of a dialog's commit action: <see cref="Primary"/> is the accent fill; <see cref="Destructive"/>
/// is the danger-red fill for irreversible actions. Both stand apart from the secondary outline button.
/// </summary>
public enum DialogButtonRole
{
    Primary,
    Destructive,
}

/// <summary>
/// The filled commit button of a dialog — the accent or danger action that pairs with a
/// <see cref="SecondaryDialogButton"/> Cancel — with an optional leading icon (e.g. a busy spinner).
/// Exposes its <see cref="ButtonState"/> so the parent attaches a controller
/// (<c>button.WithController&lt;KbmController&gt;()</c>) and a press runs <see cref="Command"/>. Size it
/// with the inherited <c>Height</c>/<c>MinWidth</c>.
/// </summary>
internal sealed record ActionDialogButton : Widget<ButtonState>
{
    /// <summary>The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</summary>
    public ICommand? Command { get; init; }

    public required Prop<string?> Label { get; init; }

    /// <summary>Accent (<see cref="DialogButtonRole.Primary"/>) or danger (<see cref="DialogButtonRole.Destructive"/>) fill.</summary>
    public DialogButtonRole Role { get; init; } = DialogButtonRole.Primary;

    /// <summary>Optional leading glyph; collapses out of the row when null/empty.</summary>
    public Prop<string?> Icon { get; init; } = (string?)null;

    /// <summary>Leading glyph angle (radians); drive from a spinner animation while an op runs.</summary>
    public Prop<float> IconRotation { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(Command);

    protected override IWidget Build(Context ctx, ButtonState state)
    {
        var destructive = Role == DialogButtonRole.Destructive;

        // The filled roles paint their own accent/danger surface, with a matching-color 1px border so
        // they read at the same size as the secondary outline button beside them.
        return new Box
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DialogFrame.ControlBorderRadius),
            Background = Theme.Color(s => s.DialogActionButton.Fill(destructive, state)),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.DialogActionButton.Fill(destructive, state))),
            Children =
            [
                new DialogButtonContent
                {
                    Label = Label,
                    Icon = Icon,
                    IconRotation = IconRotation,
                    Tint = Theme.Color(s => s.DialogActionButton.Foreground(destructive, state)),
                },
            ],
        };
    }
}
