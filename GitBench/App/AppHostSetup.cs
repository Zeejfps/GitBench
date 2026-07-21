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
        public void UseWindowGeometry(PreferencesService preferences)
        {
            appHost.OnWindowResized += preferences.SetWindowSize;
            appHost.OnWindowMoved += preferences.SetWindowPosition;
        }

        public void UseUpdateChecks()
        {
            var services = appHost.Context;
            var updateService = services.Require<UpdateService>();
            var dispatcher = services.Require<IUiDispatcher>();
            _ = updateService.CheckForUpdatesAsync(dispatcher, userInitiated: false);
            updateService.StartAutoChecks(dispatcher);
        }

        public void UseThemedTitleBar()
        {
            var services = appHost.Context;
            var themeMode = services.Require<State<ThemeMode>>();
            appHost.SetTitleBarDark(themeMode.Value == ThemeMode.Dark);
            themeMode.Changed += mode => appHost.SetTitleBarDark(mode == ThemeMode.Dark);
        }

        // Drives the UI's base writing direction from the active locale's culture: an RTL locale
        // (Arabic) flips text alignment and the bidi base for direction-neutral lines.
        public void UseLocaleTextDirection()
        {
            var services = appHost.Context;
            var locale = services.Require<State<Locale>>();
            void Apply(Locale l) => appHost.SetBaseDirection(
                Strings.For(l).Culture.TextInfo.IsRightToLeft ? BidiDirection.Rtl : BidiDirection.Auto);
            Apply(locale.Value);
            locale.Changed += Apply;
        }

        public void UseAppFonts()
        {
            var fontAssembly = typeof(LucideIcons).Assembly;
            appHost.RegisterFont(LucideIcons.FontFamily, EmbeddedAssets.LoadBytes(fontAssembly, "Lucide.ttf"), 16);
            appHost.RegisterFont(DiffOptions.MonoFontFamily, EmbeddedAssets.LoadBytes(fontAssembly, "JetBrainsMono-Regular.ttf"), 13);

            // Glyph fallbacks come from system fonts so we don't bundle any. CJK registers one font
            // per script family (JP/SC/KR); the shape layer picks per glyph by cmap coverage. Arabic
            // (RTL) is reordered to visual order by the BiDi shape layer. Deferred off the startup
            // path: these are large system TTCs (100+ MB combined), none needed until non-Latin text
            // appears, so reading them must not block first paint. Text drawn before its fallback
            // lands shows tofu for a frame, then re-shapes when RegisterFallbackFont drops the cache.
            DeferFallbacks(appHost, "CJK", SystemFonts.CjkFallbacks());
            DeferFallbacks(appHost, "Arabic", SystemFonts.ArabicFallbacks());
        }

        public void UsePlatformIcons()
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

    // Reads each fallback font off the UI thread, then posts its registration back onto the UI
    // dispatcher (the font backend isn't thread-safe). Registration order is preserved per script.
    private static void DeferFallbacks(GuiApp appHost, string script, IReadOnlyList<SystemFontSpec> fonts)
    {
        if (fonts.Count == 0) return;
        var dispatcher = appHost.Context.Require<IUiDispatcher>();
        Task.Run(() =>
        {
            foreach (var font in fonts)
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(font.Path); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Fonts] {script} fallback read failed ({font.Path}): {ex.Message}");
                    continue;
                }
                var faceIndex = font.FaceIndex;
                var path = font.Path;
                dispatcher.Post(() =>
                {
                    try { appHost.RegisterFallbackFontFromMemory(bytes, 16, faceIndex); }
                    catch (Exception ex) { Console.WriteLine($"[Fonts] {script} fallback load failed ({path}): {ex.Message}"); }
                });
            }
        });
    }
}
