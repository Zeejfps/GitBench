using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Small icon-only button sized to fit inside the <see cref="StatusBarView"/>. The icon glyph
/// is driven through <see cref="Icon"/> so callers can swap it reactively (e.g. sun/moon for the
/// theme toggle). Chrome mirrors <see cref="DialogCloseButton"/> but uses the status-bar palette.
/// </summary>
internal sealed class StatusBarIconButton : HoverableButton
{
    public State<string> Icon { get; } = new(string.Empty);

    /// <summary>Angle (radians) of the glyph; drive it from a <see cref="SpinnerAnimation"/>
    /// to spin a <see cref="LucideIcons.Loader"/> while a background op runs.</summary>
    public State<float> IconRotation { get; } = new(0f);

    public StatusBarIconButton(Context ctx, string? tooltip = null) : base(tooltip: tooltip)
    {
        var theme = ctx.Theme();
        Width = 22;
        Height = 18;

        var label = new TextView(ctx.Canvas)
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindText(Icon);
        label.BindRotation(IconRotation);
        label.BindTextColor(() => IsHovered.Value ? theme.Styles.Value.StatusBar.IconHover : theme.Styles.Value.StatusBar.Icon);

        var background = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(4),
            Children = { label },
        };
        background.BindBackgroundColor(() => IsHovered.Value ? theme.Styles.Value.StatusBar.IconHoverBackground : 0u);

        SetBackground(background);
    }
}
