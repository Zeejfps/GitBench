using ZGF.Gui;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Infrastructure.Compat;

/// <summary>
/// Ambient window context for legacy view construction. The framework now injects
/// per-window services (canvas, input system) through constructors at build time; legacy
/// views are constructed without a context, so they pull from here instead. The main
/// window's context is the default; window factories (secondary windows, popups, menus)
/// push their own context around root construction so nested views wire to the right window.
/// </summary>
public static class CompatUi
{
    private static Context? _main;

    [ThreadStatic]
    private static Context? _ambient;

    public static Context Current => _ambient ?? _main ?? throw new InvalidOperationException(
        "No ambient window context. Call CompatUi.SetMain at startup, or CompatUi.Push inside a window's BuildRoot factory.");

    public static ICanvas Canvas => Current.Canvas;

    public static InputSystem Input => Current.Require<InputSystem>();

    public static void SetMain(Context context) => _main = context;

    public static IDisposable Push(Context context)
    {
        var previous = _ambient;
        _ambient = context;
        return new ActionDisposable(() => _ambient = previous);
    }
}
