using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

internal sealed class LocalChangesHeaderActionButton : HoverableButton
{
    private const float ButtonSize = 22f;
    private const float IconSize = 13f;

    private readonly TextView _iconView;

    public LocalChangesHeaderActionButton(string icon, Action? onClick = null, string? tooltip = null)
        : base(onClick, tooltip)
    {
        Width = ButtonSize;
        Height = ButtonSize;

        var iconView = new TextView
        {
            Text = icon,
            FontFamily = LucideIcons.FontFamily,
            FontSize = IconSize,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _iconView = iconView;
        iconView.BindThemedTextColor(s =>
        {
            if (!IsEnabled) return s.HeaderActionButton.IconDisabled;
            return IsHovered ? s.HeaderActionButton.IconHover : s.HeaderActionButton.IconIdle;
        });

        var background = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(3),
            Children = { iconView },
        };
        background.BindThemedBackgroundColor(s =>
            IsEnabled && IsHovered ? s.HeaderActionButton.BackgroundHover : s.HeaderActionButton.Background);
        SetBackground(background);
    }

    public void SetIcon(string icon) => _iconView.Text = icon;
}
