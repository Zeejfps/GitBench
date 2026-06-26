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
internal sealed record ButtonWidget : Widget<ButtonState>
{
    /// <summary>The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</summary>
    public ICommand? Command { get; init; }

    /// <summary>Visual treatment; defaults to <see cref="ButtonStyle.Plain"/>.</summary>
    public ButtonStyle Style { get; init; } = ButtonStyle.Plain;

    /// <summary>Horizontal padding around the content; defaults to the labeled inset. Icon-only
    /// buttons pass <see cref="ButtonStyle.IconOnlyInset"/>.</summary>
    public Prop<PaddingStyle> ContentInset { get; init; } = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Md };

    /// <summary>Content laid out in a row inside the button — a <see cref="ButtonIcon"/> and an
    /// optional <see cref="ButtonLabel"/>.</summary>
    public IWidget[] Children { get; init; } = [];

    protected override ButtonState CreateState(Context ctx) => new(Command);

    protected override IWidget Build(Context ctx, ButtonState state)
    {
        var content = new Foreground
        {
            Value = Style.Foreground(state),
            Child = new Row
            {
                Gap = Spacing.Sm,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children = Children,
            },
        };

        // Bare styles own no chrome — render the content directly. The chip styles wrap it in a
        // surface box with horizontal padding.
        if (!Style.HasSurface)
            return content;

        return new Box
        {
            Height = Sizes.ControlHeight,
            BorderRadius = Style.Radius,
            Background = Style.Surface(state),
            BorderColor = Style.BorderColor(state),
            BorderSize = Style.BorderSize,
            Children =
            [
                new Padding { Amount = ContentInset, Children = [content] },
            ],
        };
    }
}
