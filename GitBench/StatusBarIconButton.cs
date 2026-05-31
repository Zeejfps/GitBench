using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Small icon-only button sized to fit inside the <see cref="StatusBarView"/>. The icon glyph
/// is driven through <see cref="Icon"/> so callers can swap it reactively (e.g. sun/moon for the
/// theme toggle). Chrome mirrors <see cref="DialogCloseButton"/> but uses the status-bar palette.
/// </summary>
internal sealed class StatusBarIconButton : HoverableButton
{
    public State<string> Icon { get; } = new(string.Empty);

    public StatusBarIconButton(string? tooltip = null) : base(tooltip: tooltip)
    {
        Width = 22;
        Height = 18;

        var label = new TextView
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindText(Icon);
        label.BindThemedTextColor(s => IsHovered.Value ? s.StatusBar.IconHover : s.StatusBar.Icon);

        var background = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(4),
            Children = { label },
        };
        background.BindThemedBackgroundColor(s => IsHovered.Value ? s.StatusBar.IconHoverBackground : 0u);

        SetBackground(background);
    }
}
