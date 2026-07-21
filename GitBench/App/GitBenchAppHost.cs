using GitBench.Platform;
using ZGF.Desktop;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using static GitBench.App.AppPaths;

namespace GitBench.App;

/// <summary>
/// GitBench's composition root — preferences, services, the GUI host, fonts, menus, updates — kept
/// separate from the entry point so something other than <c>Program</c> can stand the real app up
/// in-process. That's what the automation runner does: it builds the same app the user gets and
/// scripts it, which keeps every trace of the automation out of the shipped binary.
/// </summary>
internal sealed class GitBenchAppHost : IDisposable
{
    private readonly PreferencesService _preferences;

    public GuiApp App { get; }

    private GitBenchAppHost(
        PreferencesService preferences,
        GuiApp app)
    {
        _preferences = preferences;
        App = app;
    }

    /// <param name="startUnfocused">Show the window without taking OS focus. For scripted runs: the
    /// driver injects input and needs no focus, and taking it would drop the user's own keystrokes
    /// into whatever field the script had just focused.</param>
    public static GitBenchAppHost Create(bool startUnfocused = false)
    {
        CrashLog.Install(AppDataPath("crash.log"));

        var prefsPath = AppDataPath("preferences.json");
        var preferences = new PreferencesService(PreferencesStore.Load(prefsPath), prefsPath);

        var builder = GuiApp.CreateBuilder(new StartupConfig
        {
            WindowTitle = "GitBench",
            WindowWidth = preferences.Current.WindowWidth,
            WindowHeight = preferences.Current.WindowHeight,
            WindowX = preferences.Current.WindowX,
            WindowY = preferences.Current.WindowY,
            IsUndecorated = false,
            StartUnfocused = startUnfocused,
        });

        builder.Context.AddAppServices(preferences);

        var app = builder.Build(new AppWidget());

        app.UseWindowGeometry(preferences);
        app.UseThemedTitleBar();
        app.UseLocaleTextDirection();
        app.UseAppFonts();
        app.UsePlatformIcons();
        app.UseNativeAppMenu();
        app.UseUpdateChecks();

        return new GitBenchAppHost(preferences, app);
    }

    public void Run() => App.Run();

    public void Dispose()
    {
        App.Dispose();
        _preferences.Dispose();
    }
}
