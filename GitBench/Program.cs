using System.Runtime.InteropServices;
using GitGui;
using ZGF.AppUtils;
using ZGF.Core;
using ZGF.Gui;
using ZGF.Observable;

var prefsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "GitGui",
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

var statePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "GitGui",
    "state.json");
var initialState = RepoStateStore.Load(statePath);
var registry = new RepoRegistry(initialState, statePath);
context.AddService<IRepoRegistry>(registry);
var repoActivity = new RepoActivityTracker();
context.AddService<IRepoActivityTracker>(repoActivity);
context.AddService<IGitService>(new GitService(repoActivity));
context.AddService<IDragController>(new DragController(registry));
context.AddService(new LocalChangesSelectionStore());

var appView = new AppView(preferences);
var appHost = GuiApp.CreateDefault(new StartupConfig
{
    WindowTitle = "GitGui",
    WindowWidth = initialPrefs.WindowWidth,
    WindowHeight = initialPrefs.WindowHeight,
    IsUndecorated = false
}, context, appView);
appHost.OnWindowResized += preferences.SetWindowSize;

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

appHost.Run();
