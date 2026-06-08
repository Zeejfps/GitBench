using System.Runtime.InteropServices;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Platforms.Linux;
using ZGF.Gui.Desktop.Platforms.Osx;
using ZGF.Gui.Desktop.Platforms.Windows;

namespace GitBench;

internal static class PlatformServices
{
    // Clipboard is only registered on Windows/macOS, which have native APIs; Linux (and anything
    // else) falls through to GuiApp's default, which routes through the GLFW window's connection to
    // the display server. Window chrome likewise falls through to GuiApp's no-op outside the three.
    public static void AddPlatformServices(this Context context)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            context.AddService<IPlatformShell>(new WindowsPlatformShell());
            context.AddService<IClipboard>(new Win32Clipboard());
            context.AddService<IPopupNativeDecorator>(new WindowsPopupDecorator());
            context.AddService<IWindowChrome>(new WindowsWindowChrome());
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            context.AddService<IPlatformShell>(new MacOSPlatformShell());
            context.AddService<IClipboard>(new OsxClipboard());
            context.AddService<IPopupNativeDecorator>(new MacOsPopupDecorator());
            context.AddService<IWindowChrome>(new MacOsWindowChrome());
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            context.AddService<IPlatformShell>(new LinuxPlatformShell());
            context.AddService<IPopupNativeDecorator>(new NoopPopupDecorator());
            context.AddService<IWindowChrome>(new LinuxWindowChrome());
        }
        else
        {
            context.AddService<IPlatformShell>(new NoopPlatformShell());
            context.AddService<IPopupNativeDecorator>(new NoopPopupDecorator());
        }
    }
}
