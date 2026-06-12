using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// Bindings for the bordered-button visual pattern (background + border that flip
/// between idle and hover states). Shared by <see cref="DialogButton"/>,
/// <see cref="AddRepoButton"/>, and the in-dialog dropdowns. All bindings drive
/// the <see cref="BorderedButtonStyles"/> theme group, so the chrome follows the
/// active theme automatically.
/// </summary>
internal static class BorderedButtonChrome
{
    public static void Bind(RectView background, IThemeService<ThemeStyles> theme, IReadable<bool> isHovered)
    {
        background.BindBackgroundColor(() =>
            isHovered.Value ? theme.Styles.Value.BorderedButton.BackgroundHover : theme.Styles.Value.BorderedButton.BackgroundIdle);
        background.BindBorderColor(() =>
            BorderColorStyle.All(isHovered.Value ? theme.Styles.Value.BorderedButton.BorderHover : theme.Styles.Value.BorderedButton.BorderIdle));
    }

    /// <summary>
    /// Same as the <see cref="IReadable{T}"/> overload but driven by a derived predicate —
    /// useful when "is hovered" needs to be combined with another state (e.g. disabled
    /// buttons that shouldn't react to the pointer).
    /// </summary>
    public static void Bind(RectView background, IThemeService<ThemeStyles> theme, Func<bool> isEffectivelyHovered)
    {
        background.BindThemedBackgroundColor(theme, s =>
            isEffectivelyHovered() ? s.BorderedButton.BackgroundHover : s.BorderedButton.BackgroundIdle);
        background.BindThemedBorderColor(theme, s =>
            BorderColorStyle.All(isEffectivelyHovered() ? s.BorderedButton.BorderHover : s.BorderedButton.BorderIdle));
    }
}
