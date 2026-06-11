using GitBench.Theming;
using ZGF.Gui;

namespace GitBench.Widgets;

public static class ThemeWidgetExtensions
{
    /// <summary>
    /// The window's theme service, for widget Build/CreateView code. Theme-driven props are
    /// bound with the framework's auto-tracked bindings:
    /// <c>BindColor = () => ctx.Theme().Styles.Value.DialogBody.BodyText</c>.
    /// </summary>
    public static IThemeService<ThemeStyles> Theme(this Context ctx) =>
        ctx.Require<IThemeService<ThemeStyles>>();
}
