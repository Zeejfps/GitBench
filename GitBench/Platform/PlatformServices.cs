using System.Runtime.InteropServices;
using GitBench.App;
using GitBench.Controls.Dialogs;
using GitBench.Localization;
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
                context.AddService<IPlatformShell>(new MacOSPlatformShell(context));
                context.AddService<IClipboard>(new OsxClipboard());
                context.AddService<IPopupNativeDecorator>(new MacOsPopupDecorator());
                context.AddService<IWindowChrome>(new MacOsWindowChrome());
                context.AddService<IAppMenu>(new MacOsAppMenu());
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                context.AddService<IPlatformShell>(new LinuxPlatformShell(context));
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

        public void InstallNativeAppMenu()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;

            var themeMode = context.Require<State<ThemeMode>>();
            var updateService = context.Require<UpdateService>();
            var dispatcher = context.Require<IUiDispatcher>();
            var bus = context.Require<IMessageBus>();
            var locale = context.Require<State<Locale>>();
            var loc = context.Require<ILocalizationService>();
            var appMenu = context.Require<IAppMenu>();

            void ToggleTheme() =>
                themeMode.Value = themeMode.Value == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;
            void CheckForUpdates() =>
                _ = updateService.CheckForUpdatesAsync(dispatcher, userInitiated: true);
            void ShowAbout() =>
                bus.Broadcast(new ShowDialogMessage(onClose =>
                    new AboutDialog { OnClose = onClose }.WithController<DialogKbmController>()));

            AppMenuBar BuildMenuBar()
            {
                var s = loc.Strings.Value;
                return new AppMenuBar
                {
                    Menus =
                    {
                        new AppMenu
                        {
                            Title = "Pecia",
                            Role = AppMenuRole.Application,
                            Items =
                            {
                                new AppMenuItem { Title = s.MenuAppAbout, OnClick = ShowAbout },
                                AppMenuItem.Separator,
                                new AppMenuItem { Title = s.MenuAppCheckUpdates, OnClick = CheckForUpdates },
                                AppMenuItem.Separator,
                                new AppMenuItem { Title = s.MenuAppHide, Standard = AppMenuStandardAction.Hide, KeyEquivalent = "h" },
                                new AppMenuItem
                                {
                                    Title = s.MenuAppHideOthers,
                                    Standard = AppMenuStandardAction.HideOthers,
                                    KeyEquivalent = "h",
                                    Modifiers = AppMenuModifiers.Command | AppMenuModifiers.Option,
                                },
                                new AppMenuItem { Title = s.MenuAppShowAll, Standard = AppMenuStandardAction.ShowAll },
                                AppMenuItem.Separator,
                                new AppMenuItem { Title = s.MenuAppQuit, Standard = AppMenuStandardAction.Quit, KeyEquivalent = "q" },
                            },
                        },
                        new AppMenu
                        {
                            Title = s.MenuViewTitle,
                            Items =
                            {
                                new AppMenuItem { Title = s.MenuViewToggleTheme, OnClick = ToggleTheme },
                                AppMenuItem.Separator,
                                new AppMenuItem { Title = s.MenuViewLanguageEnglish, OnClick = () => locale.Value = Locale.En },
                                new AppMenuItem { Title = s.MenuViewLanguageSpanish, OnClick = () => locale.Value = Locale.Es },
                                new AppMenuItem { Title = s.MenuViewLanguageJapanese, OnClick = () => locale.Value = Locale.Ja },
                                new AppMenuItem { Title = s.MenuViewLanguageChinese, OnClick = () => locale.Value = Locale.ZhHans },
                                new AppMenuItem { Title = s.MenuViewLanguageKorean, OnClick = () => locale.Value = Locale.Ko },
                                new AppMenuItem { Title = s.MenuViewLanguageArabic, OnClick = () => locale.Value = Locale.Ar },
                                new AppMenuItem { Title = s.MenuViewLanguageRussian, OnClick = () => locale.Value = Locale.Ru },
                                new AppMenuItem { Title = s.MenuViewLanguagePseudo, OnClick = () => locale.Value = Locale.Pseudo },
                                AppMenuItem.Separator,
                                new AppMenuItem
                                {
                                    Title = s.MenuViewFullscreen,
                                    Standard = AppMenuStandardAction.ToggleFullScreen,
                                    KeyEquivalent = "f",
                                    Modifiers = AppMenuModifiers.Command | AppMenuModifiers.Control,
                                },
                            },
                        },
                        new AppMenu
                        {
                            Title = s.MenuWindowTitle,
                            Role = AppMenuRole.Window,
                            Items =
                            {
                                new AppMenuItem { Title = s.MenuWindowMinimize, Standard = AppMenuStandardAction.Minimize, KeyEquivalent = "m" },
                                new AppMenuItem { Title = s.MenuWindowZoom, Standard = AppMenuStandardAction.Zoom },
                                AppMenuItem.Separator,
                                new AppMenuItem { Title = s.MenuWindowClose, Standard = AppMenuStandardAction.Close, KeyEquivalent = "w" },
                                AppMenuItem.Separator,
                                new AppMenuItem { Title = s.MenuWindowBringAllToFront, Standard = AppMenuStandardAction.BringAllToFront },
                            },
                        },
                    },
                };
            }

            appMenu.Install(BuildMenuBar());

            // The native bar is built once at startup; rebuild it when the locale changes so the
            // menu titles follow the active language like the rest of the UI. locale.Changed fires
            // on the UI thread (the menu items that flip it run there), so the AppKit calls are safe.
            locale.Changed += _ => appMenu.Install(BuildMenuBar());
        }
    }
}
