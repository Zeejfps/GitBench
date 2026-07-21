using GitBench.Features.Identity;
using GitBench.Platform;
using ZGF.Desktop;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Observable;
using static GitBench.App.AppPaths;

namespace GitBench.App;

/// <summary>
/// GitBench's composition root — preferences, services, the GUI host, fonts, menus, updates — kept
/// separate from the entry point so something other than <c>Program</c> can stand the real app up
/// in-process. That's what the automation runner does: it builds the same app the user gets and
/// scripts it, which keeps every trace of the automation out of the shipped binary.
/// </summary>
internal sealed class GitBenchApp : IDisposable
{
    private readonly PreferencesService _preferences;
    private readonly IdentityProfileService _identityProfiles;

    public GuiApp App { get; }
    public Context Context { get; }

    private GitBenchApp(
        PreferencesService preferences,
        IdentityProfileService identityProfiles,
        Context context,
        GuiApp app)
    {
        _preferences = preferences;
        _identityProfiles = identityProfiles;
        Context = context;
        App = app;
    }

    /// <param name="startUnfocused">Show the window without taking OS focus. For scripted runs: the
    /// driver injects input and needs no focus, and taking it would drop the user's own keystrokes
    /// into whatever field the script had just focused.</param>
    public static GitBenchApp Create(bool startUnfocused = false)
    {
        CrashLog.Install(AppDataPath("crash.log"));

        var prefsPath = AppDataPath("preferences.json");
        var preferences = new PreferencesService(PreferencesStore.Load(prefsPath), prefsPath);

        var profilesPath = AppDataPath("identity-profiles.json");
        var identityProfiles = new IdentityProfileService(IdentityProfileStore.Load(profilesPath), profilesPath);

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

        var context = builder.Services;
        context.AddAppServices(preferences, identityProfiles, AppDataPath("state.json"));

        var app = builder.UseContent(ctx => new AppView().BuildView(ctx)).Build();

        app.PersistWindowGeometry(preferences);
        app.StartBackgroundServices(context);
        app.BindTitleBarToTheme(context);
        app.BindTextDirectionToLocale(context);
        app.RegisterAppFonts();
        app.LoadPlatformIcons();
        app.InstallNativeAppMenu(context);
        app.StartUpdateChecks(context);

        return new GitBenchApp(preferences, identityProfiles, context, app);
    }

    public void Run() => App.Run();

    public void Dispose()
    {
        App.Dispose();
        Context.Dispose();
        _identityProfiles.Dispose();
        _preferences.Dispose();
    }
}
