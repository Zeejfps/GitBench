using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

internal sealed class ThemeService : IThemeService<ThemeStyles>, IDisposable
{
    private readonly Derived<ThemeStyles> _styles;

    public IReadable<ThemeStyles> Styles => _styles;

    public ThemeService(State<ThemeMode> mode)
    {
        _styles = new Derived<ThemeStyles>(() =>
            mode.Value == ThemeMode.Dark ? ThemeStyles.Dark : ThemeStyles.Light);
    }

    public void Dispose() => _styles.Dispose();
}
