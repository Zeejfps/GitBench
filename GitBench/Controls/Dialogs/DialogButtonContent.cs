using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// The inner face shared by the dialog button variants: a centered label with an optional leading glyph
/// (e.g. a busy spinner), tinted by <see cref="Tint"/> and laid out with the standard horizontal inset.
/// Stateless — the surrounding chrome and the interaction state belong to the variant that wraps it.
/// </summary>
internal sealed record DialogButtonContent : Widget
{
    public required Prop<string?> Label { get; init; }

    /// <summary>Glyph/label color for this button's role and current interaction state.</summary>
    public required Prop<uint> Tint { get; init; }

    /// <summary>Optional leading glyph; collapses out of the row (no slot, no gap) when null/empty.</summary>
    // A set null constant by default, not Unset: the glyph is always rendered and derives its own
    // visibility from this, so the "no icon" case must still evaluate to a real false rather than be skipped.
    public Prop<string?> Icon { get; init; } = (string?)null;

    /// <summary>Leading glyph angle (radians); drive from a spinner animation while an op runs.</summary>
    public Prop<float> IconRotation { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var label = new Text
        {
            Value = Label,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Foreground.Color,
        };

        // The glyph hides itself when null/empty, and the flex row skips hidden children (no slot, no
        // gap), so a label-only button stays centered without a separate code path.
        var icon = new Text
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Value = Icon,
            Rotation = IconRotation,
            Color = Foreground.Color,
            Visible = Icon.Select(i => !string.IsNullOrEmpty(i)),
        };

        // Horizontal padding gives short labels breathing room and lets the button size to its text
        // (clamped up by MinWidth in DialogFrame.ButtonsRow).
        return new Padding
        {
            Amount = new PaddingStyle { Left = Spacing.Xl, Right = Spacing.Xl },
            Children =
            [
                new Foreground
                {
                    Value = Tint,
                    Child = new Row
                    {
                        Gap = Spacing.Sm,
                        MainAxis = MainAxisAlignment.Center,
                        CrossAxis = CrossAxisAlignment.Center,
                        Children = [icon, label],
                    },
                },
            ],
        };
    }
}
