using GitBench.App;
using GitBench.Features.Identity;
using GitBench.Platform;
using Velopack;
using ZGF.Desktop;
using ZGF.Gui.Desktop;
using ZGF.Observable;
using static GitBench.App.AppPaths;

// Must be the very first thing that runs: Velopack's install/update hooks are driven by
// special CLI args the installer/updater passes, and any file I/O or GUI work before this
// can leave an update half-applied. No callbacks here — the DI container doesn't exist yet.
VelopackApp.Build().Run();

var prefsPath = AppDataPath("preferences.json");
using var preferences = new PreferencesService(PreferencesStore.Load(prefsPath), prefsPath);

var profilesPath = AppDataPath("identity-profiles.json");
using var identityProfiles = new IdentityProfileService(IdentityProfileStore.Load(profilesPath), profilesPath);

var builder = GuiApp.CreateBuilder(new StartupConfig
{
    WindowTitle = "GitBench",
    WindowWidth = preferences.Current.WindowWidth,
    WindowHeight = preferences.Current.WindowHeight,
    IsUndecorated = false
});
using var services = builder.Services;
services.AddAppServices(preferences, identityProfiles, AppDataPath("state.json"));

using var appHost = builder.UseContent(ctx => new AppView().BuildView(ctx)).Build();
appHost.OnWindowResized += preferences.SetWindowSize;

// The dispatcher is registered during Build, so background services (watchers, sync, stores)
// can only spin up now.
services.CreateEagerSingletons();

appHost.BindTitleBarToTheme(services);
appHost.BindTextDirectionToLocale(services);
appHost.RegisterAppFonts();
appHost.LoadPlatformIcons();
services.InstallNativeAppMenu();

var updateService = services.Require<UpdateService>();
var updateDispatcher = services.Require<IUiDispatcher>();
_ = updateService.CheckForUpdatesAsync(updateDispatcher, userInitiated: false);
updateService.StartAutoChecks(updateDispatcher);

appHost.Run();
