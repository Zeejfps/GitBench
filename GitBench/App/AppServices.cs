using GitBench.Controls;
using GitBench.Features.Identity;
using GitBench.Features.LocalChanges;
using GitBench.Features.Notifications;
using GitBench.Features.Operations;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Localization;
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

        var locale = new State<Locale>(preferences.Current.Language);
        locale.Changed += preferences.SetLanguage;
        context.AddService(locale);
        context.AddSingleton<ILocalizationService, LocalizationService>();

        context.AddPlatformServices();

        context.AddSingleton<IRepoRegistry>(_ =>
            new RepoRegistry(RepoStateStore.Load(statePath), statePath));
        // Defers the all-repos startup sweeps (status / worktree / submodule) behind the active
        // repo's first load so they don't contend with it. Resolved by the stores/services below.
        context.AddSingleton<IStartupSweepCoordinator, StartupSweepCoordinator>();
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
        context.AddSingleton<RepoHoverState>();
        context.AddSingleton(ctx => new RepoNodeFactory(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IRepoStatusStore>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<IGitService>(),
            ctx.Get<IPlatformShell>(),
            ctx.Require<ILocalizationService>(),
            ctx.Get<IClipboard>(),
            ctx.Require<IUiDispatcher>()));
        context.AddSingleton<LocalChangesSelectionStore>();
        context.AddSingleton<OperationViewModel>();
        // Shared so the Local Changes file list and the workspace-footer merge bar drive the same
        // staging / commit state from either tab.
        context.AddSingleton<LocalChangesViewModel>();
        context.AddSingleton<UpdateService>();

        // Review windows' data seam: the real base..head range source (first-parent, merge-base
        // anchored). StubReviewStackSource remains as the Phase-3 reference impl behind this seam.
        context.AddSingleton<IReviewStackSource, GitReviewStackSource>();

        context.AddSingleton<IRepoSnapshotStore>(ctx =>
        {
            var store = ctx.Require<RepoSnapshotStore>();
            store.Start(ctx.Require<IUiDispatcher>());
            return store;
        }, eager: true);
        context.AddSingleton<IRepoOperationsStore>(ctx =>
        {
            var store = ctx.Require<RepoOperationsStore>();
            store.Start(ctx.Require<IUiDispatcher>());
            return store;
        }, eager: true);
        context.AddSingleton<IRepoStatusStore>(ctx =>
        {
            var store = ctx.Require<RepoStatusStore>();
            store.Start(ctx.Require<IUiDispatcher>());
            return store;
        }, eager: true);

        context.AddSingleton<IToastService>(ctx =>
        {
            var toasts = ctx.Require<ToastService>();
            toasts.Start(ctx.Require<IUiDispatcher>());
            return toasts;
        }, eager: true);

        context.AddSingleton<ITooltipService>(ctx => new PopupTooltipService(
            ctx.Require<IPopupWindowFactory>(),
            ctx.Require<IWindowCoordinates>()));

        context.AddSingleton<RepoWatcherService>(eager: true);
        context.AddSingleton<WorktreeSyncService>(eager: true);
        context.AddSingleton<SubmoduleSyncService>(eager: true);
        context.AddSingleton<SubmodulePointerSyncService>(eager: true);
    }
}
