using ZGF.Gui;
using ZGF.Gui.Desktop;

namespace GitBench;

internal static class FocusExtensions
{
    public static void Blur(this Context? context, IKeyboardMouseController controller)
        => context?.Get<InputSystem>()?.Blur(controller);

    public static void StealFocus(this Context? context, IKeyboardMouseController controller)
        => context?.Get<InputSystem>()?.StealFocus(controller);
}
