using System.Runtime.InteropServices;
using GitBench.App;
using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.Desktop;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Hosting;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Platform;

internal static class PlatformServices
{
    // The clipboard falls through to GuiApp's default where no entry registers one.
    extension(Context context)
    {
        public void AddPlatformServices()
        {
            // The OS credential store (DPAPI / Keychain / Secret Service), under
            // ISecretStore. The folder name is the app's stable identity: change it and saved
            // secrets stop resolving.
            context.AddNativeSecretStore(AppIdentity.DataFolderName);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                context.AddService<IPlatformShell>(new WindowsPlatformShell());
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                context.AddService<IPlatformShell>(new MacOSPlatformShell());
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                context.AddService<IPlatformShell>(new LinuxPlatformShell());
            }
            else
            {
                context.AddService<IPlatformShell>(new NoopPlatformShell());
            }
        }
    }

    extension(GuiApp app)
    {
        public void UseNativeAppMenu()
        {
            var menuBar = app.SystemMenuBar;
            if (menuBar == null) return;

            var context = app.Context;
            var themeMode = context.Require<State<ThemeMode>>();
            var updateService = context.Require<UpdateService>();
            var dispatcher = context.Require<IUiDispatcher>();
            var bus = context.Require<IMessageBus>();
            var locale = context.Require<State<Locale>>();
            var loc = context.Require<ILocalizationService>();
            var actions = new Dictionary<AppMenuItem, Action>();

            void ToggleTheme() =>
                themeMode.Value = themeMode.Value == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;
            void CheckForUpdates() =>
                _ = updateService.CheckForUpdatesAsync(dispatcher, userInitiated: true);
            void ShowAbout() =>
                bus.Broadcast(new ShowDialogMessage(onClose =>
                    new AboutDialog { OnClose = onClose }.WithController<DialogKbmController>()));
            void ShowSettings() =>
                bus.Broadcast(new OpenSettingsWindowMessage());

            AppMenuItem Item(string key, string title, Action action, Shortcut? shortcut = null)
            {
                var item = new AppMenuItem(key, title, shortcut, AppMenuSystemAction.None);
                actions[item] = action;
                return item;
            }

            static AppMenuItem SystemItem(string key, string title, AppMenuSystemAction action, Shortcut? shortcut = null) =>
                new(key, title, shortcut, action);

            List<AppMenu> BuildMenus()
            {
                actions.Clear();
                var s = loc.Strings.Value;
                return
                [
                    new AppMenu("app", AppIdentity.DisplayName, AppMenuRole.Standard)
                    {
                        Entries =
                        {
                            Item("about", s.MenuAppAbout(AppIdentity.DisplayName), ShowAbout),
                            new AppMenuSeparator(),
                            Item("settings", s.MenuAppSettings, ShowSettings, Shortcut.Command(KeyboardKey.Comma)),
                            new AppMenuSeparator(),
                            Item("check-updates", s.MenuAppCheckUpdates, CheckForUpdates),
                            new AppMenuSeparator(),
                            SystemItem("hide", s.MenuAppHide(AppIdentity.DisplayName), AppMenuSystemAction.Hide, Shortcut.Command(KeyboardKey.H)),
                            SystemItem("hide-others", s.MenuAppHideOthers, AppMenuSystemAction.HideOthers, Shortcut.Command(KeyboardKey.H, ShortcutModifiers.Alt)),
                            SystemItem("show-all", s.MenuAppShowAll, AppMenuSystemAction.ShowAll),
                            new AppMenuSeparator(),
                            SystemItem("quit", s.MenuAppQuit(AppIdentity.DisplayName), AppMenuSystemAction.Quit, Shortcut.Command(KeyboardKey.Q)),
                        },
                    },
                    new AppMenu("view", s.MenuViewTitle, AppMenuRole.Standard)
                    {
                        Entries =
                        {
                            Item("toggle-theme", s.MenuViewToggleTheme, ToggleTheme),
                            new AppMenuSeparator(),
                            SystemItem("fullscreen", s.MenuViewFullscreen, AppMenuSystemAction.ToggleFullScreen, Shortcut.Command(KeyboardKey.F, ShortcutModifiers.Control)),
                        },
                    },
                    new AppMenu("window", s.MenuWindowTitle, AppMenuRole.Window)
                    {
                        Entries =
                        {
                            SystemItem("minimize", s.MenuWindowMinimize, AppMenuSystemAction.Minimize, Shortcut.Command(KeyboardKey.M)),
                            SystemItem("zoom", s.MenuWindowZoom, AppMenuSystemAction.Zoom),
                            new AppMenuSeparator(),
                            SystemItem("close", s.MenuWindowClose, AppMenuSystemAction.Close, Shortcut.Command(KeyboardKey.W)),
                            new AppMenuSeparator(),
                            SystemItem("bring-all-to-front", s.MenuWindowBringAllToFront, AppMenuSystemAction.BringAllToFront),
                        },
                    },
                ];
            }

            void Picked(AppMenuItem item)
            {
                if (actions.TryGetValue(item, out var action)) action();
            }

            menuBar.Show(BuildMenus(), Picked);

            // Rebuilt when the locale changes so the menu titles follow the active language like the
            // rest of the UI. locale.Changed fires on the UI thread, so the AppKit calls are safe.
            locale.Changed += _ => menuBar.Show(BuildMenus(), Picked);
        }
    }
}
