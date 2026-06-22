using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// The secondary (outline) dialog button — Cancel, and other non-committal actions like "Browse…" or
/// the commit-bar commit button — in the shared bordered-button chrome, with an optional leading icon.
/// Exposes its <see cref="ButtonState"/> so the parent attaches a controller
/// (<c>button.WithController&lt;KbmController&gt;()</c>) and a press runs <see cref="Command"/>. Size it
/// with the inherited <c>Height</c>/<c>MinWidth</c>.
/// </summary>
internal sealed record SecondaryDialogButton : Widget<ButtonState>
{
    /// <summary>The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</summary>
    public ICommand? Command { get; init; }

    public required Prop<string?> Label { get; init; }

    /// <summary>Optional leading glyph; collapses out of the row when null/empty.</summary>
    public Prop<string?> Icon { get; init; } = (string?)null;

    /// <summary>Leading glyph angle (radians); drive from a spinner animation while an op runs.</summary>
    public Prop<float> IconRotation { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(Command);

    protected override IWidget Build(Context ctx, ButtonState state) => new BorderedButtonSurface
    {
        State = state,
        Radius = DialogFrame.ControlBorderRadius,
        Children =
        [
            new DialogButtonContent
            {
                Label = Label,
                Icon = Icon,
                IconRotation = IconRotation,
                Tint = Theme.Color(s => s.BorderedButton.Foreground(state)),
            },
        ],
    };
}
