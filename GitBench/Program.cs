using System.Runtime.InteropServices;
using GitGui;
using Velopack;
using Velopack.Sources;
using ZGF.AppUtils;
using ZGF.Core;
using ZGF.Gui;
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

var context = new Context();
context.AddService(preferences);
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
    : new NoopPlatformShell());
context.AddService<IClipboard>(
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new Win32Clipboard()
    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? new OsxClipboard()
    : new AppClipboard());

context.AddService<IPopupNativeDecorator>(
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new WindowsPopupDecorator()
    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? new MacOsPopupDecorator()
    : new NoopPopupDecorator());

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    context.AddService<IWindowChrome>(new WindowsWindowChrome());
else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    context.AddService<IWindowChrome>(new MacOsWindowChrome());

var statePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "GitBench",
    "state.json");
var initialState = RepoStateStore.Load(statePath);
var registry = new RepoRegistry(initialState, statePath);
context.AddService<IRepoRegistry>(registry);
var repoActivity = new RepoActivityTracker();
context.AddService<IRepoActivityTracker>(repoActivity);
context.AddService<IGitService>(new GitService(repoActivity));
context.AddService<IDragController>(new DragController(registry));
context.AddService(new LocalChangesSelectionStore());

var updateService = new UpdateService();
context.AddService(updateService);

var appView = new AppView(preferences, updateService);
var appHost = GuiApp.CreateDefault(new StartupConfig
{
    WindowTitle = "GitBench",
    WindowWidth = initialPrefs.WindowWidth,
    WindowHeight = initialPrefs.WindowHeight,
    IsUndecorated = false
}, context, appView);
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

// Check GitHub Releases for an update off the UI thread. The per-OS/arch channel must match
// the --channel vpk packs with in CI (see .github/workflows/release.yml). Velopack only finds
// updates from an installed build, so a plain `dotnet run` quietly no-ops via the catch.
static string RuntimeChannel() =>
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64"
    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
        : "linux-x64";

Func<Task> checkForUpdatesAsync = async () =>
{
    try
    {
        var source = new GithubSource("https://github.com/Zeejfps/GitBench", null, false);
        var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = RuntimeChannel() });
        var update = await manager.CheckForUpdatesAsync();
        if (update is null) return;
        await manager.DownloadUpdatesAsync(update);
        // Marshal back to the UI thread — State<T>/views are single-threaded.
        context.Require<IUiDispatcher>().Post(() => updateService.OfferUpdate(manager, update));
    }
    catch
    {
        // Offline, no published release for this channel yet, or a non-installed dev build.
    }
};
_ = Task.Run(checkForUpdatesAsync);

appHost.Run();
