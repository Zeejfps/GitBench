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
/// fill. Static config and auto-tracked bindings flow through init-only props; the click is
/// wired through <see cref="Command"/> (busy/disable-aware) or <see cref="OnClick"/>.
/// </summary>
internal sealed record ActionButton : Widget
{
    public required string Icon { get; init; }
    public string? Label { get; init; }
    public string? Tooltip { get; init; }

    /// <summary>Auto-tracked icon glyph binding; overrides <see cref="Icon"/> once mounted.</summary>
    public Func<string>? BindIcon { get; init; }

    /// <summary>Auto-tracked label binding; overrides <see cref="Label"/> once mounted.</summary>
    public Func<string>? BindLabel { get; init; }

    /// <summary>Enables the count badge next to the icon and selects its themed color.</summary>
    public Func<ThemeStyles, uint>? BadgeColor { get; init; }

    /// <summary>Auto-tracked badge count; null or 0 hides the badge.</summary>
    public Func<int?>? BindBadge { get; init; }

    public uint? IconColor { get; init; }
    public uint? Background { get; init; }

    /// <summary>Glyph angle (radians); drive from a spinner animation while an op runs.</summary>
    public IReadable<float>? IconRotation { get; init; }

    public ICommand? Command { get; init; }
    public Action? OnClick { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var theme = ctx.Theme();
        var hovered = new State<bool>(false);
        var iconColor = IconColor ?? (Background != null ? 0xFFFFFFFFu : (uint?)null);
        var command = Command;
        var onClick = OnClick;

        bool Enabled() => command?.CanExecute.Value ?? true;

        uint SelectBackground(ThemeStyles s)
        {
            if (Background is uint bg)
            {
                if (!Enabled()) return Darken(bg, 0x40);
                return hovered.Value ? Lighten(bg, 0x18) : bg;
            }
            return Enabled() && hovered.Value ? s.ActionButton.BackgroundHover : s.ActionButton.BackgroundIdle;
        }

        uint SelectForeground(ThemeStyles s)
        {
            if (!Enabled()) return s.ActionButton.TextDisabled;
            if (BindBadge?.Invoke() is > 0 && BadgeColor != null) return BadgeColor(s);
            if (iconColor is uint ic) return ic;
            return hovered.Value ? s.ActionButton.TextHover : s.ActionButton.TextIdle;
        }

        uint SelectLabelForeground(ThemeStyles s)
        {
            if (!Enabled()) return s.ActionButton.TextDisabled;
            // On a colored-background button the label must match the (white) icon, not the
            // theme's default text color — otherwise dark text on the green fill is unreadable.
            if (iconColor is uint ic) return ic;
            return hovered.Value ? s.ActionButton.TextHover : s.ActionButton.TextIdle;
        }

        Prop<string?> iconValue = BindIcon != null ? Prop.Bind(() => (string?)BindIcon()) : Icon;
        var icon = new Text
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 15,
            VAlign = TextAlignment.Center,
            Value = iconValue,
            Color = Prop.Bind(() => SelectForeground(theme.Styles.Value)),
            Rotation = IconRotation != null ? Prop.Bind(IconRotation) : Prop<float>.Unset,
        };

        var iconGroupChildren = new List<IWidget> { icon };
        if (BadgeColor is { } badgeColor)
        {
            iconGroupChildren.Add(new Text
            {
                VAlign = TextAlignment.Center,
                Color = Theme.Color(badgeColor),
                Value = Prop.Bind<string?>(() => BindBadge?.Invoke()?.ToString() ?? string.Empty),
                Visible = Prop.Bind(() => BindBadge?.Invoke() is > 0),
            });
        }

        var countIconGroup = new Row
        {
            Gap = 0,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children = [.. iconGroupChildren],
        };

        var rowChildren = new List<IWidget> { countIconGroup };
        var hasLabel = Label != null || BindLabel != null;
        if (hasLabel)
        {
            Prop<string?> labelValue = BindLabel != null ? Prop.Bind(() => (string?)BindLabel()) : Label;
            rowChildren.Add(new Text
            {
                VAlign = TextAlignment.Center,
                Value = labelValue,
                Color = Prop.Bind(() => SelectLabelForeground(theme.Styles.Value)),
            });
        }

        var horizontalPadding = hasLabel ? 8 : (Background != null ? 10 : 6);
        var box = new Box
        {
            Height = 28,
            BorderRadius = Background != null ? BorderRadiusStyle.All(6) : default,
            Padding = new PaddingStyle { Left = horizontalPadding, Right = horizontalPadding },
            Background = Prop.Bind(() => SelectBackground(theme.Styles.Value)),
            Children =
            [
                new Row
                {
                    Gap = 6,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children = [.. rowChildren],
                },
            ],
        };

        IWidget interactive = new KbmInput
        {
            OnClick = () =>
            {
                if (command is { } cmd) cmd.Execute();
                else onClick?.Invoke();
            },
            OnHoverEnter = () => hovered.Value = true,
            OnHoverExit = () => hovered.Value = false,
            Child = box,
        };

        if (Tooltip is { Length: > 0 } tooltipText)
        {
            IReadable<bool> isEnabled = command?.CanExecute ?? new State<bool>(true);
            interactive = interactive.Use(v => new Tooltip(v, ctx, tooltipText, hovered, isEnabled));
        }

        return interactive;
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
        var r = ((argb >> 16) & 0xFF);
        var g = ((argb >> 8) & 0xFF);
        var b = (argb & 0xFF);
        r = r > delta ? r - delta : 0;
        g = g > delta ? g - delta : 0;
        b = b > delta ? b - delta : 0;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }
}
