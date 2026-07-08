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
        public void BindTitleBarToTheme(Context services)
        {
            var themeMode = services.Require<State<ThemeMode>>();
            appHost.SetTitleBarDark(themeMode.Value == ThemeMode.Dark);
            themeMode.Changed += mode => appHost.SetTitleBarDark(mode == ThemeMode.Dark);
        }

        // Drives the UI's base writing direction from the active locale's culture: an RTL locale
        // (Arabic) flips text alignment and the bidi base for direction-neutral lines.
        public void BindTextDirectionToLocale(Context services)
        {
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

            // The About dialog shows the app icon, so load it into the canvas up front. Scoped to
            // macOS — the only place the dialog is currently reachable — where Metal texture upload
            // needs no current GL context. A load failure just falls back to a glyph.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                try { AboutDialog.IconImageId = appHost.LoadImage("Assets/commit_bench_icon_mac.png"); }
                catch (Exception ex) { Console.WriteLine($"[About] icon load failed: {ex.Message}"); }
            }
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
