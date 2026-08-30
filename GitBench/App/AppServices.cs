using GitBench.Controls;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Commits;
using GitBench.Features.FileBrowser;
using GitBench.Features.Identity;
using GitBench.Features.LocalChanges;
using GitBench.Features.Notifications;
using GitBench.Features.Operations;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Submodules;
using GitBench.Features.Terminal;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Pty;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Observable;

namespace GitBench.App;

internal static class AppServices
{
    // One client for the app's lifetime. No timeout: a turn streams for as long as the model takes,
    // and cancellation is the CancellationToken's job, not the client's.
    private static readonly HttpClient AssistantHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

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

        // The one source of truth for the opt-in core.untrackedCache setting: the status-bar
        // settings toggle writes it and GitUntrackedCacheService reads it, so the two can't
        // disagree about the current value.
        var enableUntrackedCache = new State<bool>(preferences.Current.EnableUntrackedCache);
        enableUntrackedCache.Changed += preferences.SetEnableUntrackedCache;
        context.AddService(enableUntrackedCache);

        context.AddPlatformServices();

        var statePath = AppPaths.AppDataPath("state.json");
        context.AddSingleton<IRepoRegistry>(_ =>
            new RepoRegistry(RepoStateStore.Load(statePath), statePath));
        // Defers the all-repos startup sweeps (status / worktree / submodule) behind the active
        // repo's first load so they don't contend with it. Resolved by the stores/services below.
        context.AddSingleton<AppViewModel>();
        context.AddSingleton<IStartupSweepCoordinator, StartupSweepCoordinator>();
        // The one throttle every background git read shares, so a many-repo tree can't seek-thrash
        // one disk. Injected into the two stores and the coordinator below; reads only — mutations
        // serialize on GitRepoLocks and never touch it.
        context.AddSingleton<IGitReadGate, GitReadGate>();
        context.AddSingleton<IRepoActivityTracker, RepoActivityTracker>();
        // One GitService, registered under every capability it implements as well as the whole
        // IGitService. Consumers depend on the narrowest facet they use; the instance is added
        // rather than factory-registered so the fourteen keys can't ever mean fourteen owners.
        var gitService = new GitService(context.Require<IRepoActivityTracker>());
        context.AddService<IGitService>(gitService);
        context.AddService<IGitRepositoryReader>(gitService);
        context.AddService<IGitStatusReader>(gitService);
        context.AddService<IGitDiffReader>(gitService);
        context.AddService<IGitHistoryReader>(gitService);
        context.AddService<IGitBranchOperations>(gitService);
        context.AddService<IGitWorkingTreeOperations>(gitService);
        context.AddService<IGitStashOperations>(gitService);
        context.AddService<IGitRemoteOperations>(gitService);
        context.AddService<IGitTagOperations>(gitService);
        context.AddService<IGitIntegrationOperations>(gitService);
        context.AddService<IGitConflictOperations>(gitService);
        context.AddService<IGitWorktreeOperations>(gitService);
        context.AddService<IGitSubmoduleOperations>(gitService);
        context.AddService<IGitConfigOperations>(gitService);
        context.AddService<IGitRawConfigReader>(gitService);
        // Reads config through gitService and back-wires itself into it (its hosted Start) so every
        // git invocation gets the right per-repo name/email/SSH key injected without touching repo
        // config. Hosted via a factory because its deps need an interface cast the container can't do.
        context.AddHostedService(ctx => new GitIdentityService(
            ctx.Require<IGitRawConfigReader>(), ctx.Require<IdentityProfileService>(),
            ctx.Require<IMessageBus>(), (IIdentityOverrides)ctx.Require<IRepoRegistry>()));
        context.AddSingleton<IDragController, DragController>();
        context.AddSingleton<RepoHoverState>();
        context.AddSingleton<RepoBarCollapseState>();
        context.AddSingleton(ctx => new RepoNodeFactory(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IRepoStatusStore>(),
            ctx.Require<IRepoLoadStore>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<IGitRemoteOperations>(),
            ctx.Require<IGitWorktreeOperations>(),
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
                ctx.Require<IGitHistoryReader>(),
                ctx.Require<IGitDiffReader>(),
                ctx.Require<IGitWorkingTreeOperations>(),
                ctx.Require<IGitConflictOperations>(),
                ctx.Require<IGitSubmoduleOperations>(),
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

        // The terminal pane's two halves: what spawns the shell, and what parses what it writes.
        // Both stateless, and both registered rather than constructed at the pane — this is the only
        // place that names a concrete VT engine, so replacing XtermSharp is a line here.
        context.AddSingleton<IPtySessionFactory, PtySessionFactory>();
        context.AddSingleton<ITerminalEngineFactory, XtermSharpEngineFactory>();
        // The terminals themselves: one per repository, in memory for the app session, so switching
        // repositories swaps which shell the pane shows rather than retargeting the one it has.
        // Hosted for the same reason the assistant's store is — it watches the registry, which it
        // can only do once the UI loop exists.
        context.AddHostedService<ITerminalSessionStore, TerminalSessionStore>();

        context.AddSingleton<IFileSystemReader, FileSystemReader>();
        context.AddHostedService<IFileBrowserStore, FileBrowserStore>();

        // Factory because the snapshot store ingests the active repo's file-list summary into the
        // status store, an interface cast (IRepoStatusIngest) the container can't do by plain
        // injection — the same shape GitIdentityService uses above. IRepoStatusIngest is deliberately
        // not its own registration: the container owns every factory result, so a second delegating
        // registration would dispose RepoStatusStore twice.
        context.AddHostedService<IRepoSnapshotStore, RepoSnapshotStore>(ctx => new RepoSnapshotStore(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IGitHistoryReader>(),
            ctx.Require<IGitStatusReader>(),
            ctx.Require<IGitBranchOperations>(),
            ctx.Require<IGitSubmoduleOperations>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<IStartupSweepCoordinator>(),
            (IRepoStatusIngest)ctx.Require<IRepoStatusStore>(),
            ctx.Require<IGitReadGate>(),
            ctx.Require<IUiDispatcher>()));
        context.AddHostedService<IRepoOperationsStore, RepoOperationsStore>();
        // Samples the read gate + the operations store once a frame into the per-repo "loading" flag
        // the RepoBar rows spin on. Registered after both, and hosted so its frame tick starts with
        // the rest of the app rather than on first row build.
        context.AddHostedService<IRepoLoadStore, RepoLoadStore>();
        // The head store owns the checkout; the status store composes its pending branch into
        // RepoStatus and, in return, tells it when a fresh read has landed. Same factory-plus-cast
        // shape as the ingest wiring above, and for the same reason: the container can't cast, and a
        // second delegating registration would dispose the store twice.
        context.AddSingleton<IRepoHeadStore, RepoHeadStore>();
        context.AddHostedService<IRepoStatusStore, RepoStatusStore>(ctx => new RepoStatusStore(
            ctx.Require<IRepoOperationsStore>(),
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IGitStatusReader>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<IStartupSweepCoordinator>(),
            ctx.Require<IGitReadGate>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IRepoHeadStore>(),
            (IRepoHeadConfirm)ctx.Require<IRepoHeadStore>()));
        // Pushes the active repo's id into the read gate, which is what makes the gate admit that
        // repo's reads ahead of the startup sweep instead of behind it.
        context.AddHostedService<GitReadPriorityService>();

        // The assistant's conversations, one per repo, in memory for the app session. The backend is
        // built by the store rather than registered on its own: it needs a live read of the
        // connection the store resolves off the UI thread, which a plain registration would make
        // circular. Which provider that is survives restarts the way the theme and language do.
        var assistantSettings = new State<AssistantSettings>(AssistantSettings.From(
            preferences.Current.AssistantProviderId,
            preferences.Current.AssistantProviderPreferences
                .Select(c => (c.ProviderId, c.Model, c.BaseUrl))));
        assistantSettings.Changed += s => preferences.SetAssistantProvider(
            s.ProviderId,
            s.Choices.Select(c => new AssistantProviderPreference(c.Key, c.Value.Model, c.Value.BaseUrl)).ToArray());
        context.AddService(assistantSettings);
        context.AddSingleton(ctx => new AssistantCredentials(ctx.Require<ISecretStore>()));
        context.AddHostedService<IAssistantSessionStore, AssistantSessionStore>(ctx => new AssistantSessionStore(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IGitService>(),
            ctx.Require<AssistantCredentials>(),
            ctx.Require<State<AssistantSettings>>(),
            ctx.Require<ILocalizationService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<LocalChangesViewModel>(),
            ctx.Require<IReviewProgressStore>(),
            ctx.Require<IRepoOperationsStore>(),
            connection => new AssistantBackendRouter(AssistantHttp, connection)));
        context.AddSingleton<AssistantPanelPlacement>();
        context.AddSingleton<AssistantViewModel>();

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
        // Applies the opt-in core.untrackedCache setting to managed primaries; its three deps
        // (registry, git service, the enable-untracked-cache observable) are all registered above,
        // so plain reflective ctor injection resolves it.
        context.AddHostedService<GitUntrackedCacheService>();
    }
}
