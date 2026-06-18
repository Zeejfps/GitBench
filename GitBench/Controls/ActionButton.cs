using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// Toolbar/banner action button: an icon with an optional label, count badge, and solid fill. Live
/// hover/press/enabled state lives on an <see cref="ActionButtonState"/> the widget exposes as its
/// <see cref="IInteractable"/> surface, so the <em>parent</em> attaches a controller
/// (<c>button.WithController&lt;KbmController&gt;()</c>) and a press runs <see cref="Command"/> — whose
/// <see cref="ICommand.CanExecute"/> gates the button and drives its disabled look.
/// </summary>
internal sealed record ActionButton : Widget<ActionButtonState>
{
    /// <summary>Icon glyph; a constant or an auto-tracked binding (<c>Prop.Bind(() =&gt; …)</c>).</summary>
    public required Prop<string?> Icon { get; init; }

    /// <summary>Optional label next to the icon; unset renders icon-only.</summary>
    public Prop<string?> Label { get; init; }

    /// <summary>Hover tooltip; unset (or empty) shows none.</summary>
    public Prop<string?> Tooltip { get; init; }

    /// <summary>Count shown in a badge next to the icon; null or 0 hides it.</summary>
    public Prop<int?> Badge { get; init; }

    /// <summary>Badge text color — bind a theme color with <see cref="GitBench.Widgets.Theme.Color"/>.</summary>
    public Prop<uint> BadgeColor { get; init; }

    /// <summary>Solid fill; when set the button paints a filled, rounded chip with white glyphs.</summary>
    public Prop<uint?> Background { get; init; }

    /// <summary>Glyph angle (radians); drive from a spinner animation while an op runs.</summary>
    public Prop<float> IconRotation { get; init; }

    /// <summary>The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</summary>
    public ICommand? Command { get; init; }

    protected override ActionButtonState CreateState(Context ctx) =>
        new(Command, Background.ToReadable(ctx));

    protected override IWidget Build(Context ctx, ActionButtonState state)
    {
        var foreground = Theme.Color(s => s.ActionButton.Foreground(state));

        IWidget button = new Box
        {
            Height = 28,
            BorderRadius = Prop.Bind(() => state.Fill.Value is null ? default : BorderRadiusStyle.All(6)),
            Background = Theme.Color(s => s.ActionButton.Surface(state)),
            Children =
            [
                new Padding
                {
                    Amount = Prop.Bind(() => HorizontalPadding(state.Fill.Value)),
                    Children = [Content(foreground)],
                },
            ],
        };

        if (!Tooltip.IsSet) return button;
        var tooltip = Tooltip.ToReadable(ctx);
        return button.Use(v => new Tooltip(v, ctx, tooltip, state.Hovered, state.Enabled));
    }

    private IWidget Content(Prop<uint> foreground)
    {
        var icon = new Text
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 15,
            VAlign = TextAlignment.Center,
            Value = Icon,
            Color = foreground,
            Rotation = IconRotation,
        };

        IWidget glyphs = !Badge.IsSet ? icon : new Row
        {
            Gap = 0,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                icon,
                new Text
                {
                    VAlign = TextAlignment.Center,
                    Color = BadgeColor,
                    Value = Badge.Select(count => count?.ToString()),
                    Visible = Badge.Select(count => count is > 0),
                },
            ],
        };

        if (!Label.IsSet) return glyphs;

        return new Row
        {
            Gap = 6,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                glyphs,
                new Text { VAlign = TextAlignment.Center, Value = Label, Color = foreground },
            ],
        };
    }

    private PaddingStyle HorizontalPadding(uint? fill)
    {
        var pad = Label.IsSet ? 8 : fill is null ? 6 : 10;
        return new PaddingStyle { Left = pad, Right = pad };
    }
}
