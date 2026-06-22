using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// A square, icon-only button over the shared <see cref="ButtonState"/> primitive: a Lucide glyph
/// centered in a themed surface box. The caller supplies the surface/glyph color ramps, read against
/// the live interaction state. Live state is exposed as the widget's <see cref="IInteractable"/>
/// surface, so the parent attaches a controller (<c>button.WithController&lt;KbmController&gt;()</c>)
/// and an optional tooltip (<c>button.WithTooltip("…")</c>), and a press runs <see cref="Command"/>.
/// Size it with the inherited <c>Width</c>/<c>Height</c>.
/// </summary>
internal sealed record IconButtonWidget : Widget<ButtonState>
{
    /// <summary>The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</summary>
    public ICommand? Command { get; init; }

    /// <summary>Icon glyph; a constant or an auto-tracked binding (<c>Prop.Bind(() =&gt; …)</c>).</summary>
    public required Prop<string?> Icon { get; init; }

    public float IconSize { get; init; } = 14f;

    /// <summary>Glyph angle (radians); drive from a spinner animation while an op runs.</summary>
    public Prop<float> Rotation { get; init; }

    public Prop<BorderRadiusStyle> CornerRadius { get; init; } = BorderRadiusStyle.All(4);

    /// <summary>Box fill, resolved against the button's interaction state.</summary>
    public required Func<IInteractable, Prop<uint>> Surface { get; init; }

    /// <summary>Glyph color, resolved against the button's interaction state.</summary>
    public required Func<IInteractable, Prop<uint>> Foreground { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(Command);

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        BorderRadius = CornerRadius,
        Background = Surface(state),
        Children =
        [
            new Text
            {
                FontFamily = LucideIcons.FontFamily,
                FontSize = IconSize,
                HAlign = TextAlignment.Center,
                VAlign = TextAlignment.Center,
                Value = Icon,
                Rotation = Rotation,
                Color = Foreground(state),
            },
        ],
    };
}
