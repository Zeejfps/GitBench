using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Bindings for the bordered-button visual pattern (background + border that flip
/// between idle and hover states). Shared by <see cref="DialogButton"/>,
/// <see cref="AddRepoButton"/>, and the in-dialog dropdowns. All bindings drive
/// the <see cref="BorderedButtonStyles"/> theme group, so the chrome follows the
/// active theme automatically.
/// </summary>
internal static class BorderedButtonChrome
{
    public static void Bind(RectView background, IReadable<bool> isHovered)
    {
        background.BindThemedBackgroundColor(s =>
            isHovered.Value ? s.BorderedButton.BackgroundHover : s.BorderedButton.BackgroundIdle);
        background.BindThemedBorderColor(s =>
            BorderColorStyle.All(isHovered.Value ? s.BorderedButton.BorderHover : s.BorderedButton.BorderIdle));
    }

    /// <summary>
    /// Same as the <see cref="IReadable{T}"/> overload but driven by a derived predicate —
    /// useful when "is hovered" needs to be combined with another state (e.g. disabled
    /// buttons that shouldn't react to the pointer).
    /// </summary>
    public static void Bind(RectView background, Func<bool> isEffectivelyHovered)
    {
        background.BindThemedBackgroundColor(s =>
            isEffectivelyHovered() ? s.BorderedButton.BackgroundHover : s.BorderedButton.BackgroundIdle);
        background.BindThemedBorderColor(s =>
            BorderColorStyle.All(isEffectivelyHovered() ? s.BorderedButton.BorderHover : s.BorderedButton.BorderIdle));
    }
}
