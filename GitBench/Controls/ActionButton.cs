using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// An action button: a hover/press/enabled-driven box wrapping caller-supplied content (typically a
/// <see cref="ButtonIcon"/> and an optional <see cref="ButtonLabel"/>). <see cref="Style"/> picks the
/// look — the plain themed toolbar button or a solid filled chip. Live state lives on an
/// <see cref="ButtonState"/> exposed as the widget's <see cref="IInteractable"/> surface, so the
/// <em>parent</em> attaches a controller (<c>button.WithController&lt;KbmController&gt;()</c>) and an
/// optional tooltip (<c>button.WithTooltip("…")</c>), and a press runs <see cref="Command"/>. The
/// themed glyph/label color is published to the content subtree via a <see cref="Foreground"/> scope.
/// </summary>
internal sealed record ActionButton : Widget<ButtonState>
{
    /// <summary>The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</summary>
    public ICommand? Command { get; init; }

    /// <summary>Visual treatment; defaults to <see cref="ButtonStyle.Plain"/>.</summary>
    public ButtonStyle Style { get; init; } = ButtonStyle.Plain;

    /// <summary>Horizontal padding around the content; defaults to the labeled inset. Icon-only
    /// buttons pass <see cref="ButtonStyle.IconOnlyInset"/>.</summary>
    public Prop<PaddingStyle> ContentInset { get; init; } = new PaddingStyle { Left = 8, Right = 8 };

    /// <summary>Content laid out in a row inside the button — a <see cref="ButtonIcon"/> and an
    /// optional <see cref="ButtonLabel"/>.</summary>
    public IWidget[] Children { get; init; } = [];

    protected override ButtonState CreateState(Context ctx) => new(Command);

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        Height = 28,
        BorderRadius = Style.Radius,
        Background = Style.Surface(state),
        Children =
        [
            new Padding
            {
                Amount = ContentInset,
                Children =
                [
                    new Foreground
                    {
                        Value = Style.Foreground(state),
                        Child = new Row
                        {
                            Gap = 6,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children = Children,
                        },
                    },
                ],
            },
        ],
    };
}
