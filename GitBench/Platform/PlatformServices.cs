using System.Runtime.InteropServices;
using GitBench.App;
using GitBench.Controls.Dialogs;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Platforms.Linux;
using ZGF.Gui.Desktop.Platforms.Osx;
using ZGF.Gui.Desktop.Platforms.Windows;
using ZGF.Observable;

namespace GitBench.Platform;

internal static class PlatformServices
{
    // Clipboard is only registered on Windows/macOS, which have native APIs; Linux (and anything
    // else) falls through to GuiApp's default, which routes through the GLFW window's connection to
    // the display server. Window chrome likewise falls through to GuiApp's no-op outside the three.
    extension(Context context)
    {
        public void AddPlatformServices()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                context.AddService<IPlatformShell>(new WindowsPlatformShell());
                context.AddService<IClipboard>(new Win32Clipboard());
                context.AddService<IPopupNativeDecorator>(new WindowsPopupDecorator());
                context.AddService<IWindowChrome>(new WindowsWindowChrome());
                context.AddService<IAppMenu>(new NoopAppMenu());
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                context.AddService<IPlatformShell>(new MacOSPlatformShell());
                context.AddService<IClipboard>(new OsxClipboard());
                context.AddService<IPopupNativeDecorator>(new MacOsPopupDecorator());
                context.AddService<IWindowChrome>(new MacOsWindowChrome());
                context.AddService<IAppMenu>(new MacOsAppMenu());
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                context.AddService<IPlatformShell>(new LinuxPlatformShell());
                context.AddService<IPopupNativeDecorator>(new NoopPopupDecorator());
                context.AddService<IWindowChrome>(new LinuxWindowChrome());
                context.AddService<IAppMenu>(new NoopAppMenu());
            }
            else
            {
                context.AddService<IPlatformShell>(new NoopPlatformShell());
                context.AddService<IPopupNativeDecorator>(new NoopPopupDecorator());
                context.AddService<IAppMenu>(new NoopAppMenu());
            }
        }

        public void InstallNativeAppMenu(State<ThemeMode> themeMode,
            UpdateService updateService,
            IUiDispatcher dispatcher)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;

            var bus = context.Require<IMessageBus>();

            void ToggleTheme() =>
                themeMode.Value = themeMode.Value == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;
            void CheckForUpdates() =>
                _ = updateService.CheckForUpdatesAsync(dispatcher, userInitiated: true);
            void ShowAbout() =>
                bus.Broadcast(new ShowDialogMessage(onClose =>
                    new AboutDialog { OnClose = onClose }.WithController<DialogKbmController>()));

            context.Require<IAppMenu>().Install(new AppMenuBar
            {
                Menus =
                {
                    new AppMenu
                    {
                        Title = "GitBench",
                        Role = AppMenuRole.Application,
                        Items =
                        {
                            new AppMenuItem { Title = "About GitBench", OnClick = ShowAbout },
                            AppMenuItem.Separator,
                            new AppMenuItem { Title = "Check for Updates…", OnClick = CheckForUpdates },
                            AppMenuItem.Separator,
                            new AppMenuItem { Title = "Hide GitBench", Standard = AppMenuStandardAction.Hide, KeyEquivalent = "h" },
                            new AppMenuItem
                            {
                                Title = "Hide Others",
                                Standard = AppMenuStandardAction.HideOthers,
                                KeyEquivalent = "h",
                                Modifiers = AppMenuModifiers.Command | AppMenuModifiers.Option,
                            },
                            new AppMenuItem { Title = "Show All", Standard = AppMenuStandardAction.ShowAll },
                            AppMenuItem.Separator,
                            new AppMenuItem { Title = "Quit GitBench", Standard = AppMenuStandardAction.Quit, KeyEquivalent = "q" },
                        },
                    },
                    new AppMenu
                    {
                        Title = "View",
                        Items =
                        {
                            new AppMenuItem { Title = "Toggle Light/Dark Theme", OnClick = ToggleTheme },
                            AppMenuItem.Separator,
                            new AppMenuItem
                            {
                                Title = "Enter Full Screen",
                                Standard = AppMenuStandardAction.ToggleFullScreen,
                                KeyEquivalent = "f",
                                Modifiers = AppMenuModifiers.Command | AppMenuModifiers.Control,
                            },
                        },
                    },
                    new AppMenu
                    {
                        Title = "Window",
                        Role = AppMenuRole.Window,
                        Items =
                        {
                            new AppMenuItem { Title = "Minimize", Standard = AppMenuStandardAction.Minimize, KeyEquivalent = "m" },
                            new AppMenuItem { Title = "Zoom", Standard = AppMenuStandardAction.Zoom },
                            AppMenuItem.Separator,
                            new AppMenuItem { Title = "Close Window", Standard = AppMenuStandardAction.Close, KeyEquivalent = "w" },
                            AppMenuItem.Separator,
                            new AppMenuItem { Title = "Bring All to Front", Standard = AppMenuStandardAction.BringAllToFront },
                        },
                    },
                },
            });
        }
    }
}
