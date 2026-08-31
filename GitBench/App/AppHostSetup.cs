using System.Runtime.InteropServices;
using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.Terminal;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Messages;
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

        /// <summary>
        /// Holds the application open when a terminal still has a shell, and asks first. Every way
        /// the OS raises a close arrives here — the title-bar button, Alt+F4, and macOS's Quit, which
        /// asks the window to close rather than terminating outright.
        /// </summary>
        public void UseQuitConfirmation()
        {
            var services = appHost.Context;
            var terminals = services.Require<ITerminalSessionStore>();
            var dispatcher = services.Require<IUiDispatcher>();
            var bus = services.Require<IMessageBus>();

            appHost.OnCloseRequested += request =>
            {
                var running = terminals.ReposWithLiveShells();
                if (running.Count == 0) return;

                request.Cancel();

                // Posted rather than shown here: this runs inside the OS event poll, and the dialog
                // wants a settled view tree. The tick that drains this queue is the next thing the
                // run loop does, so the prompt still lands in the frame the user asked to close.
                dispatcher.Post(() => bus.Broadcast(new ShowDialogMessage(onClose => new ConfirmQuitDialog
                {
                    RepoIds = running,
                    OnClose = onClose,
                    // Past the guard deliberately: the user has just answered the question it asks.
                    OnConfirm = appHost.Quit,
                })));
            };
        }

        /// <summary>
        /// Builds the shared highlighter on a worker before anything asks it for colors. First
        /// touch compiles thirteen tree-sitter queries and a TextMate registry, and whichever
        /// surface reached it first used to pay for that — as a stall on the first file opened.
        /// </summary>
        public void UseWarmHighlighter()
            => Task.Run(() => _ = RoutedSyntaxHighlighter.Shared);

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
            appHost.RegisterFont(SetiIcons.FontFamily, EmbeddedAssets.LoadBytes(fontAssembly, "Seti.ttf"), 16);
            appHost.RegisterFont(MonoFonts.Regular, EmbeddedAssets.LoadBytes(fontAssembly, "JetBrainsMono-Regular.ttf"), 13);
            appHost.RegisterFont(MonoFonts.Bold, EmbeddedAssets.LoadBytes(fontAssembly, "JetBrainsMono-Bold.ttf"), 13);
            appHost.RegisterFont(MonoFonts.Italic, EmbeddedAssets.LoadBytes(fontAssembly, "JetBrainsMono-Italic.ttf"), 13);
            appHost.RegisterFont(MonoFonts.BoldItalic, EmbeddedAssets.LoadBytes(fontAssembly, "JetBrainsMono-BoldItalic.ttf"), 13);
            appHost.RegisterFont(MarkdownFonts.ItalicFamily, EmbeddedAssets.LoadBytes(fontAssembly, "Inter-Italic.ttf"), 16);

            // Glyph fallbacks come from system fonts so we don't bundle any. CJK registers one font
            // per script family (JP/SC/KR); the shape layer picks per glyph by cmap coverage. Arabic
            // (RTL) is reordered to visual order by the BiDi shape layer. Deferred off the startup
            // path: these are large system TTCs (100+ MB combined), none needed until non-Latin text
            // appears, so reading them must not block first paint. Text drawn before its fallback
            // lands shows tofu for a frame, then re-shapes when RegisterFallbackFont drops the cache.
            //
            // Symbols go first because the chain is first-cover-wins and the CJK faces overlap it:
            // AppleSDGothicNeo carries U+2610 and U+273D, so registering it earlier would draw a
            // terminal's checkbox from a proportional Korean face in a monospaced grid.
            DeferFallbacks(appHost,
            [
                ("Symbols", SystemFonts.SymbolFallbacks()),
                ("CJK", SystemFonts.CjkFallbacks()),
                ("Arabic", SystemFonts.ArabicFallbacks()),
            ]);
        }

        public void UsePlatformIcons()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                appHost.SetIcon("Assets/app_icon.rgba");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                MacOsDockIcon.Set(PathUtils.ResolveLocalPath("Assets/app_icon_mac.png"));

            // The About dialog and welcome screen show the app icon, so load it into the canvas up
            // front. GL texture upload needs the main context current (a no-op on Metal). macOS
            // gets the bundle-style artwork. A load failure just falls back to a glyph.
            var iconPng = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "Assets/app_icon_mac.png"
                : "Assets/app_icon.png";
            try
            {
                appHost.MakeMainContextCurrent();
                AppLogo.IconImageId.Value = appHost.LoadImage(iconPng);
            }
            catch (Exception ex) { Console.WriteLine($"[AppLogo] icon load failed: {ex.Message}"); }

            // Loaded separately so a missing mark can't take the app icon down with it.
            try
            {
                appHost.MakeMainContextCurrent();
                AssistantMark.ImageId.Value = appHost.LoadImage("Assets/assistant_mark.png");
            }
            catch (Exception ex) { Console.WriteLine($"[AssistantMark] mark load failed: {ex.Message}"); }
        }
    }

    // Reads the fallback fonts off the UI thread, then posts each registration back onto the UI
    // dispatcher (the font backend isn't thread-safe). One task walking every script in the order
    // given, rather than a task per script, because the chain is first-cover-wins and the scripts
    // overlap on code points: racing the readers would let the disk decide which face draws a
    // shared glyph, differently between runs.
    private static void DeferFallbacks(
        GuiApp appHost, IReadOnlyList<(string Script, IReadOnlyList<SystemFontSpec> Fonts)> scripts)
    {
        var dispatcher = appHost.Context.Require<IUiDispatcher>();
        Task.Run(() =>
        {
            foreach (var (script, fonts) in scripts)
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
            }
        });
    }
}
