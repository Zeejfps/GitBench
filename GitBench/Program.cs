using System.Runtime.InteropServices;
using GitBench;
using Velopack;
using ZGF.AppUtils;
using ZGF.Desktop;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Platforms.Linux;
using ZGF.Gui.Desktop.Platforms.Osx;
using ZGF.Gui.Desktop.Platforms.Windows;
using ZGF.Observable;

// Must be the very first thing that runs: Velopack's install/update hooks are driven by
// special CLI args the installer/updater passes, and any file I/O or GUI work before this
// can leave an update half-applied. No callbacks here — the DI container doesn't exist yet.
VelopackApp.Build().Run();

var prefsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "GitBench",
    "preferences.json");
using var preferences = new PreferencesService(PreferencesStore.Load(prefsPath), prefsPath);
var initialPrefs = preferences.Current;

var profilesPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "GitBench",
    "identity-profiles.json");
using var identityProfiles = new IdentityProfileService(IdentityProfileStore.Load(profilesPath), profilesPath);

var builder = GuiApp.CreateBuilder(new StartupConfig
{
    WindowTitle = "GitBench",
    WindowWidth = initialPrefs.WindowWidth,
    WindowHeight = initialPrefs.WindowHeight,
    IsUndecorated = false
});
var context = builder.Services;
context.AddService(preferences);
context.AddService(identityProfiles);
var messageBus = new MessageBus();
context.AddService<IMessageBus>(messageBus);
context.AddService(new State<MainViewMode>(MainViewMode.LocalChanges));
var themeMode = new State<ThemeMode>(initialPrefs.Theme);
themeMode.Changed += preferences.SetTheme;
context.AddService(themeMode);
context.AddService<IThemeService<ThemeStyles>>(new ThemeService(themeMode));
context.AddService<IPlatformShell>(
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new WindowsPlatformShell()
    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? new MacOSPlatformShell()
    : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? new LinuxPlatformShell()
    : new NoopPlatformShell());
// Windows/macOS use their native clipboard APIs. Linux (and anything else) falls through to
// GuiApp's default, which routes through the GLFW window's connection to the display server.
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    context.AddService<IClipboard>(new Win32Clipboard());
else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    context.AddService<IClipboard>(new OsxClipboard());

context.AddService<IPopupNativeDecorator>(
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new WindowsPopupDecorator()
    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? new MacOsPopupDecorator()
    : new NoopPopupDecorator());

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    context.AddService<IWindowChrome>(new WindowsWindowChrome());
else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    context.AddService<IWindowChrome>(new MacOsWindowChrome());
else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    context.AddService<IWindowChrome>(new LinuxWindowChrome());

var statePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "GitBench",
    "state.json");
var initialState = RepoStateStore.Load(statePath);
var registry = new RepoRegistry(initialState, statePath);
context.AddService<IRepoRegistry>(registry);
var repoActivity = new RepoActivityTracker();
context.AddService<IRepoActivityTracker>(repoActivity);
var gitService = new GitService(repoActivity);
context.AddService<IGitService>(gitService);
// Build the identity resolver (it reads config through gitService) then attach it back, so every
// git invocation gets the right per-repo name/email/SSH key injected without touching repo config.
var identityService = new GitIdentityService(gitService, identityProfiles, messageBus, registry);
context.AddService(identityService);
gitService.AttachIdentityResolver(identityService);
context.AddService<IDragController>(new DragController(registry));
context.AddService(new LocalChangesSelectionStore());

var updateService = new UpdateService();
context.AddService(updateService);

// Single source of truth for the active repo's loaded git data. Registered before the GUI is
// built so view models can resolve it during startup; its loading is wired in Start() below,
// once GuiApp has created the UI dispatcher.
using var snapshotStore = new RepoSnapshotStore(registry, context.Require<IGitService>(), messageBus);
context.AddService<IRepoSnapshotStore>(snapshotStore);

var appView = new AppView(preferences, updateService);
using var appHost = builder.UseContent(appView).Build();
appHost.OnWindowResized += preferences.SetWindowSize;

// Match the native title bar to the active theme, and keep it in sync on toggle.
appHost.SetTitleBarDark(themeMode.Value == ThemeMode.Dark);
themeMode.Changed += mode => appHost.SetTitleBarDark(mode == ThemeMode.Dark);

var fontAssembly = typeof(LucideIcons).Assembly;
appHost.RegisterFont(LucideIcons.FontFamily, EmbeddedAssets.LoadBytes(fontAssembly, "Lucide.ttf"), 16);
appHost.RegisterFont(DiffOptions.MonoFontFamily, EmbeddedAssets.LoadBytes(fontAssembly, "JetBrainsMono-Regular.ttf"), 13);

context.AddService<ITooltipService>(new PopupTooltipService(
    context.Require<IPopupWindowFactory>(),
    context.Require<IWindowCoordinates>(),
    measureContext: context));

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    appHost.SetIcon("Assets/commit_bench_icon.rgba");

using var repoWatchers = new RepoWatcherService(
    registry,
    context.Require<IUiDispatcher>(),
    messageBus,
    repoActivity);

using var worktreeSync = new WorktreeSyncService(
    registry,
    context.Require<IGitService>(),
    context.Require<IUiDispatcher>(),
    messageBus);

using var submoduleSync = new SubmoduleSyncService(
    registry,
    context.Require<IGitService>(),
    context.Require<IUiDispatcher>(),
    messageBus);

using var submodulePointerSync = new SubmodulePointerSyncService(
    registry,
    context.Require<IGitService>(),
    context.Require<IUiDispatcher>(),
    messageBus);

snapshotStore.Start(context.Require<IUiDispatcher>());

_ = updateService.CheckForUpdatesAsync(context.Require<IUiDispatcher>(), userInitiated: false);

appHost.Run();
