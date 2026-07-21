using System.Runtime.InteropServices;
using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using ZGF.AppUtils;
using ZGF.Fonts;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>Wires the built window host to app-wide state, fonts, and platform icons.</summary>
internal static class AppHostSetup
{
    extension(GuiApp appHost)
    {
        public void PersistWindowGeometry(PreferencesService preferences)
        {
            appHost.OnWindowResized += preferences.SetWindowSize;
            appHost.OnWindowMoved += preferences.SetWindowPosition;
        }

        // The dispatcher is registered during Build, so background services (watchers, sync, stores)
        // can only spin up now.
        public void StartBackgroundServices()
        {
            appHost.Context.CreateEagerSingletons();
        }

        public void StartUpdateChecks()
        {
            var services = appHost.Context;
            services.CreateEagerSingletons();
            var updateService = services.Require<UpdateService>();
            var dispatcher = services.Require<IUiDispatcher>();
            _ = updateService.CheckForUpdatesAsync(dispatcher, userInitiated: false);
            updateService.StartAutoChecks(dispatcher);
        }

        public void BindTitleBarToTheme()
        {
            var services = appHost.Context;
            var themeMode = services.Require<State<ThemeMode>>();
            appHost.SetTitleBarDark(themeMode.Value == ThemeMode.Dark);
            themeMode.Changed += mode => appHost.SetTitleBarDark(mode == ThemeMode.Dark);
        }

        // Drives the UI's base writing direction from the active locale's culture: an RTL locale
        // (Arabic) flips text alignment and the bidi base for direction-neutral lines.
        public void BindTextDirectionToLocale()
        {
            var services = appHost.Context;
            var locale = services.Require<State<Locale>>();
            void Apply(Locale l) => appHost.SetBaseDirection(
                Strings.For(l).Culture.TextInfo.IsRightToLeft ? BidiDirection.Rtl : BidiDirection.Auto);
            Apply(locale.Value);
            locale.Changed += Apply;
        }

        public void RegisterAppFonts()
        {
            var fontAssembly = typeof(LucideIcons).Assembly;
            appHost.RegisterFont(LucideIcons.FontFamily, EmbeddedAssets.LoadBytes(fontAssembly, "Lucide.ttf"), 16);
            appHost.RegisterFont(DiffOptions.MonoFontFamily, EmbeddedAssets.LoadBytes(fontAssembly, "JetBrainsMono-Regular.ttf"), 13);

            // Glyph fallbacks come from system fonts so we don't bundle any. CJK registers one font
            // per script family (JP/SC/KR); the shape layer picks per glyph by cmap coverage. Arabic
            // (RTL) is reordered to visual order by the BiDi shape layer.
            RegisterFallbacks(appHost, "CJK", SystemFonts.CjkFallbacks());
            RegisterFallbacks(appHost, "Arabic", SystemFonts.ArabicFallbacks());
        }

        public void LoadPlatformIcons()
        {
            // macOS is excluded: GLFW can't set the Dock icon there; it comes from the .app bundle.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                appHost.SetIcon("Assets/commit_bench_icon.rgba");

            // The About dialog and welcome screen show the app icon, so load it into the canvas up
            // front. GL texture upload needs the main context current (a no-op on Metal). macOS
            // gets the bundle-style artwork. A load failure just falls back to a glyph.
            var iconPng = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "Assets/commit_bench_icon_mac.png"
                : "Assets/commit_bench_icon.png";
            try
            {
                appHost.MakeMainContextCurrent();
                AppLogo.IconImageId.Value = appHost.LoadImage(iconPng);
            }
            catch (Exception ex) { Console.WriteLine($"[AppLogo] icon load failed: {ex.Message}"); }
        }
    }

    private static void RegisterFallbacks(GuiApp appHost, string script, IReadOnlyList<SystemFontSpec> fonts)
    {
        foreach (var font in fonts)
        {
            try { appHost.RegisterFallbackFont(font.Path, 16, font.FaceIndex); }
            catch (Exception ex) { Console.WriteLine($"[Fonts] {script} fallback load failed ({font.Path}): {ex.Message}"); }
        }
    }
}
