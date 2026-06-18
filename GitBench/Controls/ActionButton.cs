using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;
using ThemeStyles = GitBench.Theming.ThemeStyles;

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

    public string? Tooltip { get; init; }

    /// <summary>Count shown in a badge next to the icon; null or 0 hides it.</summary>
    public Prop<int?> Badge { get; init; }

    /// <summary>Badge text color — bind a theme color with <see cref="GitBench.Widgets.Theme.Color"/>.</summary>
    public Prop<uint> BadgeColor { get; init; }

    /// <summary>Solid fill; when set the button paints a filled, rounded chip with white glyphs.</summary>
    public uint? Background { get; init; }

    /// <summary>Glyph angle (radians); drive from a spinner animation while an op runs.</summary>
    public Prop<float> IconRotation { get; init; }

    /// <summary>The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</summary>
    public ICommand? Command { get; init; }

    protected override ActionButtonState CreateState(Context ctx) => new(Command);

    protected override IWidget Build(Context ctx, ActionButtonState state)
    {
        var foreground = Theme.Color(s => Foreground(s, state));

        IWidget button = new Box
        {
            Height = 28,
            BorderRadius = Background is null ? default : BorderRadiusStyle.All(6),
            Background = Theme.Color(s => Surface(s, state)),
            Children =
            [
                new Padding
                {
                    Amount = HorizontalPadding(),
                    Children = [Content(foreground)],
                },
            ],
        };

        return Tooltip is { Length: > 0 } tooltip
            ? button.Use(v => new Tooltip(v, ctx, tooltip, state.Hovered, state.Enabled))
            : button;
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

    private PaddingStyle HorizontalPadding()
    {
        var pad = Label.IsSet ? 8 : Background is null ? 6 : 10;
        return new PaddingStyle { Left = pad, Right = pad };
    }

    // Glyph/label color: white on a solid fill, otherwise the themed idle/hover/disabled ramp.
    private uint Foreground(ThemeStyles styles, IInteractable state)
    {
        var s = styles.ActionButton;
        if (!state.Enabled.Value) return s.TextDisabled;
        if (Background is not null) return 0xFFFFFFFFu;
        return state.Hovered.Value ? s.TextHover : s.TextIdle;
    }

    // Fill color: a solid button lightens on hover / darkens when disabled; a plain button uses
    // the themed idle/hover surface.
    private uint Surface(ThemeStyles styles, IInteractable state)
    {
        var s = styles.ActionButton;
        if (Background is uint fill)
        {
            if (!state.Enabled.Value) return Darken(fill, 0x40);
            return state.Hovered.Value ? Lighten(fill, 0x18) : fill;
        }
        return state.Enabled.Value && state.Hovered.Value ? s.BackgroundHover : s.BackgroundIdle;
    }

    private static uint Lighten(uint argb, uint delta)
    {
        var a = (argb >> 24) & 0xFF;
        var r = Math.Min(0xFFu, ((argb >> 16) & 0xFF) + delta);
        var g = Math.Min(0xFFu, ((argb >> 8) & 0xFF) + delta);
        var b = Math.Min(0xFFu, (argb & 0xFF) + delta);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static uint Darken(uint argb, uint delta)
    {
        var a = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        r = r > delta ? r - delta : 0;
        g = g > delta ? g - delta : 0;
        b = b > delta ? b - delta : 0;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }
}
