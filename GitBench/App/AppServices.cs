using GitBench.Controls;
using GitBench.Features.Identity;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Observable;

namespace GitBench.App;

internal static class AppServices
{
    public static void AddAppServices(
        this Context context,
        PreferencesService preferences,
        IdentityProfileService identityProfiles,
        string statePath)
    {
        context.AddService(preferences);
        context.AddService(identityProfiles);

        context.AddSingleton<IMessageBus, MessageBus>();
        context.AddService(new State<MainViewMode>(MainViewMode.LocalChanges));

        var themeMode = new State<ThemeMode>(preferences.Current.Theme);
        themeMode.Changed += preferences.SetTheme;
        context.AddService(themeMode);
        context.AddSingleton<IThemeService<ThemeStyles>, ThemeService>();

        context.AddPlatformServices();

        context.AddSingleton<IRepoRegistry>(_ =>
            new RepoRegistry(RepoStateStore.Load(statePath), statePath));
        context.AddSingleton<IRepoActivityTracker, RepoActivityTracker>();
        context.AddSingleton<IGitService>(ctx =>
            new GitService(ctx.Require<IRepoActivityTracker>()));
        // Built lazily but eagerly instantiated: it reads config through gitService and must be
        // attached back so every git invocation gets the right per-repo name/email/SSH key
        // injected without touching repo config.
        context.AddSingleton(ctx =>
        {
            var gitService = (GitService)ctx.Require<IGitService>();
            var identityService = new GitIdentityService(
                gitService, identityProfiles, ctx.Require<IMessageBus>(),
                (IIdentityOverrides)ctx.Require<IRepoRegistry>());
            gitService.AttachIdentityResolver(identityService);
            return identityService;
        }, eager: true);
        context.AddSingleton<IDragController, DragController>();
        context.AddSingleton<LocalChangesSelectionStore>();
        context.AddSingleton<UpdateService>();

        context.AddSingleton<IRepoSnapshotStore>(ctx =>
        {
            var store = ctx.Create<RepoSnapshotStore>();
            store.Start(ctx.Require<IUiDispatcher>());
            return store;
        }, eager: true);
        context.AddSingleton<IRepoOperationsStore>(ctx =>
        {
            var store = ctx.Create<RepoOperationsStore>();
            store.Start(ctx.Require<IUiDispatcher>());
            return store;
        }, eager: true);
        context.AddSingleton<IRepoStatusStore>(ctx =>
        {
            var store = ctx.Create<RepoStatusStore>();
            store.Start(ctx.Require<IUiDispatcher>());
            return store;
        }, eager: true);

        context.AddSingleton<ITooltipService>(ctx => new PopupTooltipService(
            ctx.Require<IPopupWindowFactory>(),
            ctx.Require<IWindowCoordinates>(),
            measureContext: ctx));

        context.AddSingleton<RepoWatcherService>(eager: true);
        context.AddSingleton<WorktreeSyncService>(eager: true);
        context.AddSingleton<SubmoduleSyncService>(eager: true);
        context.AddSingleton<SubmodulePointerSyncService>(eager: true);
    }
}
