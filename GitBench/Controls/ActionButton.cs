using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Widgets;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;
using ThemeStyles = GitBench.Theming.ThemeStyles;

namespace GitBench.Controls;

/// <summary>
/// Toolbar/banner action button: icon, optional label, optional count badge, optional solid
/// fill. Composes <see cref="KbmInput"/> over a themed <see cref="Box"/>; hover and enabled
/// state drive the colors. Click runs <see cref="Command"/> (which no-ops while disabled) or
/// <see cref="OnClick"/>.
/// </summary>
internal sealed record ActionButton : Widget
{
    /// <summary>Icon glyph; a constant or an auto-tracked binding (<c>Prop.Bind(() =&gt; …)</c>).</summary>
    public required Prop<string?> Icon { get; init; }

    /// <summary>Optional label next to the icon; unset renders icon-only.</summary>
    public Prop<string?> Label { get; init; }

    public string? Tooltip { get; init; }

    /// <summary>Count shown in a badge next to the icon; null or 0 hides it.</summary>
    public IReadable<int?>? Badge { get; init; }

    /// <summary>Badge text color — bind a theme color with <see cref="GitBench.Widgets.Theme.Color"/>.</summary>
    public Prop<uint> BadgeColor { get; init; }

    /// <summary>Solid fill; when set the button paints a filled, rounded chip with white glyphs.</summary>
    public uint? Background { get; init; }

    /// <summary>Glyph angle (radians); drive from a spinner animation while an op runs.</summary>
    public Prop<float> IconRotation { get; init; }

    public ICommand? Command { get; init; }
    public Action? OnClick { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var styles = ctx.Theme().Styles;
        var hovered = new State<bool>(false);
        var enabled = Command?.CanExecute;

        Prop<uint> foreground = styles.Bind(s => Foreground(s, hovered.Value, enabled));

        IWidget button = new KbmInput
        {
            OnClick = Activate,
            OnHoverEnter = () => hovered.Value = true,
            OnHoverExit = () => hovered.Value = false,
            Child = new Box
            {
                Height = 28,
                BorderRadius = Background is null ? default : BorderRadiusStyle.All(6),
                Background = styles.Bind(s => Surface(s, hovered.Value, enabled)),
                Children =
                [
                    new Padding
                    {
                        Amount = HorizontalPadding(),
                        Children = [Content(foreground)],
                    },
                ],
            },
        };

        return Tooltip is { Length: > 0 } tooltip
            ? button.Use(v => new Tooltip(v, ctx, tooltip, hovered, enabled ?? AlwaysEnabled))
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

        IWidget glyphs = Badge is null ? icon : new Row
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
                    Value = Badge.Bind(count => count?.ToString()),
                    Visible = Badge.Bind(count => count is > 0),
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

    private void Activate()
    {
        if (Command is { } command) command.Execute();
        else OnClick?.Invoke();
    }

    private PaddingStyle HorizontalPadding()
    {
        var pad = Label.IsSet ? 8 : Background is null ? 6 : 10;
        return new PaddingStyle { Left = pad, Right = pad };
    }

    // Glyph/label color: white on a solid fill, otherwise the themed idle/hover/disabled ramp.
    private uint Foreground(ThemeStyles styles, bool hovered, IReadable<bool>? enabled)
    {
        var s = styles.ActionButton;
        if (enabled is { Value: false }) return s.TextDisabled;
        if (Background is not null) return 0xFFFFFFFFu;
        return hovered ? s.TextHover : s.TextIdle;
    }

    // Fill color: a solid button lightens on hover / darkens when disabled; a plain button uses
    // the themed idle/hover surface.
    private uint Surface(ThemeStyles styles, bool hovered, IReadable<bool>? enabled)
    {
        var s = styles.ActionButton;
        if (Background is uint fill)
        {
            if (enabled is { Value: false }) return Darken(fill, 0x40);
            return hovered ? Lighten(fill, 0x18) : fill;
        }
        return enabled is not { Value: false } && hovered ? s.BackgroundHover : s.BackgroundIdle;
    }

    private static readonly State<bool> AlwaysEnabled = new(true);

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
