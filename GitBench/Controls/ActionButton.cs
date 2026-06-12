using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
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

    protected override View CreateView(Context ctx)
    {
        var view = new ButtonView(ctx, this);
        if (Command != null) view.BindCommand(Command);
        return view;
    }

    private sealed class ButtonView : HoverableButton
    {
        private readonly ActionButton _w;
        private readonly uint? _iconColor;

        public ButtonView(Context ctx, ActionButton w)
            : base(ctx, w.OnClick, w.Tooltip)
        {
            _w = w;
            _iconColor = w.IconColor ?? (w.Background != null ? 0xFFFFFFFFu : (uint?)null);
            Height = 28;

            var theme = ctx.Theme();
            var iconView = new TextView(ctx.Canvas)
            {
                FontFamily = LucideIcons.FontFamily,
                FontSize = 15,
                VerticalTextAlignment = TextAlignment.Center,
            };
            if (w.BindIcon != null) iconView.BindText(w.BindIcon);
            else iconView.Text = w.Icon;
            iconView.BindTextColor(() => SelectForeground(theme.Styles.Value));
            if (w.IconRotation != null) iconView.BindRotation(w.IconRotation);

            var countIconGroup = new RowView { Gap = 0, Children = { iconView } };

            if (w.BadgeColor != null)
            {
                var badgeText = new TextView(ctx.Canvas)
                {
                    VerticalTextAlignment = TextAlignment.Center,
                };
                badgeText.BindTextColor(() => w.BadgeColor(theme.Styles.Value));
                badgeText.BindText(() => w.BindBadge?.Invoke()?.ToString() ?? string.Empty);
                badgeText.BindIsVisible(() => w.BindBadge?.Invoke() is > 0);
                countIconGroup.Children.Add(badgeText);
            }

            var row = new RowView { Gap = 6, Children = { countIconGroup } };

            var hasLabel = w.Label != null || w.BindLabel != null;
            if (hasLabel)
            {
                var labelView = new TextView(ctx.Canvas)
                {
                    VerticalTextAlignment = TextAlignment.Center,
                };
                if (w.BindLabel != null) labelView.BindText(w.BindLabel);
                else labelView.Text = w.Label;
                labelView.BindTextColor(() => SelectLabelForeground(theme.Styles.Value));
                row.Children.Add(labelView);
            }

            var horizontalPadding = hasLabel ? 8 : (w.Background != null ? 10 : 6);
            var background = new RectView
            {
                BorderRadius = w.Background != null ? BorderRadiusStyle.All(6) : default,
                Children =
                {
                    new PaddingView
                    {
                        Padding = new PaddingStyle { Left = horizontalPadding, Right = horizontalPadding },
                        Children = { row },
                    }
                }
            };
            background.BindBackgroundColor(() => SelectBackground(theme.Styles.Value));
            SetBackground(background);
        }

        private uint SelectBackground(ThemeStyles s)
        {
            if (_w.Background is uint bg)
            {
                if (!IsEnabled) return Darken(bg, 0x40);
                return IsHovered ? Lighten(bg, 0x18) : bg;
            }
            return IsEnabled && IsHovered ? s.ActionButton.BackgroundHover : s.ActionButton.BackgroundIdle;
        }

        private uint SelectForeground(ThemeStyles s)
        {
            if (!IsEnabled) return s.ActionButton.TextDisabled;
            if (_w.BindBadge?.Invoke() is > 0 && _w.BadgeColor != null) return _w.BadgeColor(s);
            if (_iconColor is uint ic) return ic;
            return IsHovered ? s.ActionButton.TextHover : s.ActionButton.TextIdle;
        }

        private uint SelectLabelForeground(ThemeStyles s)
        {
            if (!IsEnabled) return s.ActionButton.TextDisabled;
            // On a colored-background button the label must match the (white) icon, not the
            // theme's default text color — otherwise dark text on the green fill is unreadable.
            if (_iconColor is uint ic) return ic;
            return IsHovered ? s.ActionButton.TextHover : s.ActionButton.TextIdle;
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
}
