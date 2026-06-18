using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// A bare icon button: a single Lucide glyph that runs <see cref="Command"/> on press, with no surface
/// or padding of its own (unlike <see cref="ActionButton"/>, and distinct from <see cref="ButtonIcon"/>,
/// which is the icon <em>segment</em> of one). The glyph color is resolved from the button's live
/// interaction state through <see cref="Foreground"/>, so the caller supplies the themed idle/hover/active
/// ramp. State lives on an <see cref="ActionButtonState"/> exposed as the widget's <see cref="IInteractable"/>
/// surface, so the parent attaches a controller (<c>button.WithController&lt;KbmController&gt;()</c>) and an
/// optional tooltip (<c>button.WithTooltip("…")</c>).
/// </summary>
internal sealed record IconButton : Widget<ActionButtonState>
{
    /// <summary>The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</summary>
    public required ICommand Command { get; init; }

    /// <summary>Icon glyph; a constant or an auto-tracked binding (<c>Prop.Bind(() =&gt; …)</c>).</summary>
    public required Prop<string?> Icon { get; init; }

    /// <summary>Resolves the glyph color from the button's interaction state — typically
    /// <c>s =&gt; Theme.Color(t =&gt; t.Some.Ramp(s))</c>.</summary>
    public required Func<IInteractable, Prop<uint>> Foreground { get; init; }

    public Prop<float> FontSize { get; init; } = 12f;

    protected override ActionButtonState CreateState(Context ctx) => new(Command);

    protected override IWidget Build(Context ctx, ActionButtonState state) => new Text
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize,
        HAlign = TextAlignment.Center,
        VAlign = TextAlignment.Center,
        Value = Icon,
        Color = Foreground(state),
    };
}
