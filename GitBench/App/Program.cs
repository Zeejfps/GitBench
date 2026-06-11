using System.Runtime.InteropServices;
using GitBench;
using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.Identity;
using GitBench.Features.Repos;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Theming;
using Velopack;
using ZGF.AppUtils;
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
var initialPrefs = preferences.Current;

var profilesPath = AppDataPath("identity-profiles.json");
using var identityProfiles = new IdentityProfileService(IdentityProfileStore.Load(profilesPath), profilesPath);

var builder = GuiApp.CreateBuilder(new StartupConfig
{
    WindowTitle = "GitBench",
    WindowWidth = initialPrefs.WindowWidth,
    WindowHeight = initialPrefs.WindowHeight,
    IsUndecorated = false
});
var services = builder.Services;
services.AddAppServices(preferences, identityProfiles, AppDataPath("state.json"));

var registry = services.Require<IRepoRegistry>();
var gitService = services.Require<IGitService>();
var repoActivity = services.Require<IRepoActivityTracker>();
var messageBus = services.Require<IMessageBus>();
var themeMode = services.Require<State<ThemeMode>>();
var updateService = services.Require<UpdateService>();

using var snapshotStore = new RepoSnapshotStore(registry, gitService, messageBus);
services.AddService<IRepoSnapshotStore>(snapshotStore);
using var operationsStore = new RepoOperationsStore(registry, gitService, messageBus);
services.AddService<IRepoOperationsStore>(operationsStore);
using var statusStore = new RepoStatusStore(operationsStore, registry, gitService, messageBus);
services.AddService<IRepoStatusStore>(statusStore);

var appView = new AppView(preferences, updateService);
using var appHost = builder.UseContent(appView).Build();
appHost.OnWindowResized += preferences.SetWindowSize;

appHost.SetTitleBarDark(themeMode.Value == ThemeMode.Dark);
themeMode.Changed += mode => appHost.SetTitleBarDark(mode == ThemeMode.Dark);

var fontAssembly = typeof(LucideIcons).Assembly;
appHost.RegisterFont(LucideIcons.FontFamily, EmbeddedAssets.LoadBytes(fontAssembly, "Lucide.ttf"), 16);
appHost.RegisterFont(DiffOptions.MonoFontFamily, EmbeddedAssets.LoadBytes(fontAssembly, "JetBrainsMono-Regular.ttf"), 13);

services.AddService<ITooltipService>(new PopupTooltipService(
    services.Require<IPopupWindowFactory>(),
    services.Require<IWindowCoordinates>(),
    measureContext: services));

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    appHost.SetIcon("Assets/commit_bench_icon.rgba");

var dispatcher = services.Require<IUiDispatcher>();
using var repoWatchers = new RepoWatcherService(registry, dispatcher, messageBus, repoActivity);
using var worktreeSync = new WorktreeSyncService(registry, gitService, dispatcher, messageBus);
using var submoduleSync = new SubmoduleSyncService(registry, gitService, dispatcher, messageBus);
using var submodulePointerSync = new SubmodulePointerSyncService(registry, gitService, dispatcher, messageBus);

snapshotStore.Start(dispatcher);
operationsStore.Start(dispatcher);
statusStore.Start(dispatcher);

// Native macOS menu bar (the call is macOS-guarded internally; a no-op elsewhere). The About
// dialog it opens shows the app icon, so load it into the canvas first. Scoped to macOS — the
// only place the dialog is currently reachable — where Metal texture upload needs no current
// GL context. A load failure (e.g. missing asset) just falls back to a glyph.
if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    try { AboutDialog.IconImageId = appHost.LoadImage("Assets/commit_bench_icon_mac.png"); }
    catch (Exception ex) { Console.WriteLine($"[About] icon load failed: {ex.Message}"); }
}
services.InstallNativeAppMenu(themeMode, updateService, dispatcher);

_ = updateService.CheckForUpdatesAsync(dispatcher, userInitiated: false);

appHost.Run();
