using GitBench.Theming;
using ZGF.Gui;

namespace GitBench.Widgets;

public static class ThemeWidgetExtensions
{
    /// <summary>
    /// The window's theme service, for widget Build/CreateView code. Theme-driven props bind
    /// by projecting the styles observable into a <see cref="Prop{T}"/>:
    /// <c>Color = ctx.Theme().Styles.Bind(s => s.DialogBody.BodyText)</c>.
    /// </summary>
    public static IThemeService<ThemeStyles> Theme(this Context ctx) =>
        ctx.Require<IThemeService<ThemeStyles>>();
}
