using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

public sealed class ActionButton : HoverableButton
{
    private readonly Func<ThemeStyles, uint>? _badgeColorSelect;
    private readonly uint? _iconColor;
    private readonly uint? _backgroundColor;

    public State<string> Icon { get; }
    public State<string> Label { get; }
    public State<float> IconRotation { get; } = new(0f);
    public State<int?> Badge { get; } = new(null);

    public ActionButton(string icon, string? label = null, string? tooltip = null, Func<ThemeStyles, uint>? badgeColor = null, uint? iconColor = null, uint? backgroundColor = null)
        : base(null, tooltip)
    {
        Icon = new State<string>(icon);
        Label = new State<string>(label ?? string.Empty);

        _backgroundColor = backgroundColor;
        _iconColor = iconColor ?? (backgroundColor != null ? 0xFFFFFFFFu : (uint?)null);
        _badgeColorSelect = badgeColor;
        Height = 28;

        var iconView = new TextView
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 15,
            VerticalTextAlignment = TextAlignment.Center,
        };
        iconView.BindText(Icon);
        iconView.BindThemedTextColor(SelectForeground);
        iconView.BindRotation(IconRotation);

        var countIconGroup = new RowView { Gap = 0, Children = { iconView } };

        if (badgeColor != null)
        {
            var badgeText = new TextView
            {
                VerticalTextAlignment = TextAlignment.Center,
            };
            badgeText.BindThemedTextColor(badgeColor);
            badgeText.BindText(Badge, n => n?.ToString() ?? string.Empty);
            badgeText.BindIsVisible(Badge, n => n is > 0);
            countIconGroup.Children.Add(badgeText);
        }

        var row = new RowView { Gap = 6, Children = { countIconGroup } };

        TextView? labelView = null;
        if (!string.IsNullOrEmpty(label))
        {
            labelView = new TextView
            {
                VerticalTextAlignment = TextAlignment.Center,
            };
            labelView.BindText(Label);
            labelView.BindThemedTextColor(SelectLabelForeground);
            row.Children.Add(labelView);
        }

        var horizontalPadding = labelView != null ? 8 : (_backgroundColor != null ? 10 : 6);
        var background = new RectView
        {
            BorderRadius = _backgroundColor != null ? BorderRadiusStyle.All(6) : default,
            Children =
            {
                new PaddingView
                {
                    Padding = new PaddingStyle { Left = horizontalPadding, Right = horizontalPadding },
                    Children = { row },
                }
            }
        };
        background.BindThemedBackgroundColor(SelectBackground);
        SetBackground(background);
    }

    private uint SelectBackground(ThemeStyles s)
    {
        if (_backgroundColor is uint bg)
        {
            if (!IsEnabled) return Darken(bg, 0x40);
            return IsHovered ? Lighten(bg, 0x18) : bg;
        }
        return IsEnabled && IsHovered ? s.ActionButton.BackgroundHover : s.ActionButton.BackgroundIdle;
    }

    private uint SelectForeground(ThemeStyles s)
    {
        if (!IsEnabled) return s.ActionButton.TextDisabled;
        if (Badge.Value is > 0 && _badgeColorSelect != null) return _badgeColorSelect(s);
        if (_iconColor is uint ic) return ic;
        return IsHovered ? s.ActionButton.TextHover : s.ActionButton.TextIdle;
    }

    private uint SelectLabelForeground(ThemeStyles s)
    {
        if (!IsEnabled) return s.ActionButton.TextDisabled;
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
