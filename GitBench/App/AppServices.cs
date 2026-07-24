using GitBench.Controls;
using GitBench.Features.Commits;
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
    public static void AddAppServices(this Context context, PreferencesService preferences)
    {
        context.AddService(preferences);

        var profilesPath = AppPaths.AppDataPath("identity-profiles.json");
        context.AddSingleton(_ => new IdentityProfileService(
            IdentityProfileStore.Load(profilesPath), profilesPath));

        context.AddSingleton<IMessageBus, MessageBus>();
        context.AddService(new State<MainViewMode>(MainViewMode.LocalChanges));

        // How the Changes tab presents the working tree. Shared: the toolbar toggles it, the pane
        // switches on it, and the commit bar shows staging progress only in the Diff layout.
        var workingChangesLayout = new State<WorkingChangesLayout>(preferences.Current.WorkingChangesLayout);
        workingChangesLayout.Changed += preferences.SetWorkingChangesLayout;
        context.AddService(workingChangesLayout);

        var themeMode = new State<ThemeMode>(preferences.Current.Theme);
        themeMode.Changed += preferences.SetTheme;
        context.AddService(themeMode);
        context.AddSingleton<IThemeService<ThemeStyles>, ThemeService>();

        var locale = new State<Locale>(preferences.Current.Language);
        locale.Changed += preferences.SetLanguage;
        context.AddService(locale);
        context.AddSingleton<ILocalizationService, LocalizationService>();

        context.AddPlatformServices();

        var statePath = AppPaths.AppDataPath("state.json");
        context.AddSingleton<IRepoRegistry>(_ =>
            new RepoRegistry(RepoStateStore.Load(statePath), statePath));
        // Defers the all-repos startup sweeps (status / worktree / submodule) behind the active
        // repo's first load so they don't contend with it. Resolved by the stores/services below.
        context.AddSingleton<AppViewModel>();
        context.AddSingleton<IStartupSweepCoordinator, StartupSweepCoordinator>();
        context.AddSingleton<IRepoActivityTracker, RepoActivityTracker>();
        context.AddSingleton<IGitService>(ctx =>
            new GitService(ctx.Require<IRepoActivityTracker>()));
        // Reads config through gitService and back-wires itself into it (its hosted Start) so every
        // git invocation gets the right per-repo name/email/SSH key injected without touching repo
        // config. Hosted via a factory because its deps need an interface cast the container can't do.
        context.AddHostedService(ctx => new GitIdentityService(
            (IGitRawConfigReader)ctx.Require<IGitService>(), ctx.Require<IdentityProfileService>(),
            ctx.Require<IMessageBus>(), (IIdentityOverrides)ctx.Require<IRepoRegistry>()));
        context.AddSingleton<IDragController, DragController>();
        context.AddSingleton<RepoHoverState>();
        context.AddSingleton<RepoBarCollapseState>();
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

        // The Changes tab's Review layout. Its commit-details VM is its own — opted out of the
        // selection bus so the History pane's commit selection can never drive the working-tree
        // review's file list.
        context.AddSingleton(ctx => new WorkingTreeReviewViewModel(
            ctx.Require<LocalChangesViewModel>(),
            new CommitDetailsViewModel(
                ctx.Require<IGitService>(),
                ctx.Require<IRepoRegistry>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>(),
                ctx.Require<ILocalizationService>(),
                preferences,
                subscribeToSelection: false),
            ctx.Require<IRepoRegistry>(),
            ctx.Require<ILocalizationService>()));
        context.AddSingleton<UpdateService>();

        // Review windows' data seam: the real base..head range source (first-parent, merge-base
        // anchored). StubReviewStackSource remains as the Phase-3 reference impl behind this seam.
        context.AddSingleton<IReviewStackSource, GitReviewStackSource>();

        // Review progress (marked-Viewed files) lives for the app session, shared across review
        // windows so closing and reopening a branch's review keeps its progress.
        context.AddSingleton<IReviewProgressStore, ReviewProgressStore>();

        context.AddHostedService<IRepoSnapshotStore, RepoSnapshotStore>();
        context.AddHostedService<IRepoOperationsStore, RepoOperationsStore>();
        context.AddHostedService<IRepoStatusStore, RepoStatusStore>();

        context.AddHostedService<IToastService, ToastService>();

        context.AddSingleton<ITooltipService>(ctx => new PopupTooltipService(
            ctx.Require<IPopupWindowFactory>(),
            ctx.Require<IWindowCoordinates>()));

        context.AddHostedService<RepoWatcherService>();
        // The watcher's safety net: the interval is passed here rather than defaulted in the
        // service so the app's reconcile cadence is visible at the wiring site.
        context.AddHostedService(ctx => new RepoReconcileService(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IRepoActivityTracker>(),
            ctx.Require<IAppForeground>(),
            RepoReconcileService.DefaultInterval));
        context.AddHostedService<WorktreeSyncService>();
        context.AddHostedService<SubmoduleSyncService>();
        context.AddHostedService<SubmodulePointerSyncService>();
    }
}
