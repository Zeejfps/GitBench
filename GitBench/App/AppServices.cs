using GitBench.Controls;
using GitBench.Features.AgentConnections;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Commits;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.Identity;
using GitBench.Features.LanguageServers;
using GitBench.Features.LocalChanges;
using GitBench.Features.Markdown.Rendering;
using GitBench.Features.Notifications;
using GitBench.Features.Operations;
using GitBench.Features.Pairing;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Search;
using GitBench.Features.Submodules;
using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Input;
using GitBench.Lsp.Lifecycle;
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
        context.AddService(TimeProvider.System);
        // Which keys run which commands. Every handler matches through it and every shortcut hint
        // reads from it, so a binding is decided in one table.
        var keyMap = new KeyMap(preferences.Current.KeyBindings);
        // Lives as long as the app, like the map it follows.
        _ = keyMap.Version.Subscribe(_ => preferences.Update(p => p with { KeyBindings = keyMap.Overrides }));
        context.AddService<IKeyMap>(keyMap);
        context.AddService(keyMap);

        var profilesPath = AppPaths.AppDataPath("identity-profiles.json");
        context.AddSingleton(_ => new IdentityProfileService(
            IdentityProfileStore.Load(profilesPath), profilesPath));

        context.AddSingleton<IMessageBus, MessageBus>();
        context.AddService(new State<MainViewMode>(MainViewMode.LocalChanges));
        // Which of the sidebar's two lists is on screen. App-wide rather than per-repo: it is how
        // you are navigating, not something a repository is.
        context.AddService(new State<SidebarPane>(SidebarPane.Branches));

        // How the Changes tab presents the working tree. Shared: the toolbar toggles it, the pane
        // switches on it, and the commit bar shows staging progress only in the Diff layout.
        context.Bind(preferences, p => p.WorkingChangesLayout, (p, v) => p with { WorkingChangesLayout = v });

        context.Bind(preferences, p => p.Theme, (p, v) => p with { Theme = v });
        context.AddSingleton<IThemeService<ThemeStyles>, ThemeService>();

        var uiScale = context.Bind(preferences, p => p.UiScale, (p, v) => p with { UiScale = v });
        context.AddService<IUiScale>(new PreferredUiScale(uiScale));
        // Also published as IWritable: code views that may be mounted without it probe with Get,
        // which would otherwise try to construct a State it cannot seed.
        var editorFontSize = context.Bind(preferences, p => p.EditorFontSize, (p, v) => p with { EditorFontSize = v });
        context.AddService<IWritable<EditorFontSize>>(editorFontSize);

        context.Bind(preferences, p => p.Language, (p, v) => p with { Language = v });
        context.AddSingleton<ILocalizationService, LocalizationService>();
        // One loader so decoded markdown images are shared across every surface that shows them.
        context.AddSingleton<IMarkdownImageLoader>(ctx => new MarkdownImageLoader(ctx.Require<IUiDispatcher>()));

        // The one source of truth for the opt-in core.untrackedCache setting: the status-bar
        // settings toggle writes it and GitUntrackedCacheService reads it, so the two can't
        // disagree about the current value.
        context.Bind(preferences, p => p.EnableUntrackedCache, (p, v) => p with { EnableUntrackedCache = v });

        var crashLogPath = AppPaths.AppDataPath("crash.log");
        // One grammar set behind both engines, registered under their own types as well as the
        // interfaces: the document annotator keeps a parse tree between edits, which is a seam only
        // the parser-backed engines have.
        context.AddSingleton(_ => new TreeSitterGrammars(reason => CrashLog.Note(crashLogPath, reason)));
        context.AddSingleton(ctx =>
            new TreeSitterSymbolExtractor(ctx.Require<TreeSitterGrammars>(), reason => CrashLog.Note(crashLogPath, reason)));
        context.AddAlias<ISymbolExtractor, TreeSitterSymbolExtractor>();
        context.AddSingleton(ctx =>
            new TreeSitterSyntaxHighlighter(ctx.Require<TreeSitterGrammars>(), reason => CrashLog.Note(crashLogPath, reason)));
        context.AddSingleton<SyntaxHighlighter>();
        context.AddSingleton<ISyntaxHighlighter>(ctx =>
            new RoutedSyntaxHighlighter(ctx.Require<TreeSitterSyntaxHighlighter>(), ctx.Require<SyntaxHighlighter>()));

        context.AddPlatformServices();

        var statePath = AppPaths.AppDataPath("state.json");
        context.AddSingleton(_ => new RepoRegistry(RepoStateStore.Load(statePath), statePath));
        context.AddAlias<IRepoRegistry, RepoRegistry>();
        context.AddAlias<IIdentityOverrides, RepoRegistry>();
        // Defers the all-repos startup sweeps (status / worktree / submodule) behind the active
        // repo's first load so they don't contend with it. Resolved by the stores/services below.
        context.AddSingleton<AppViewModel>();
        context.AddSingleton<StartupSweepCoordinator>();
        // The one throttle every background git read shares, so a many-repo tree can't seek-thrash
        // one disk. Injected into the two stores and the coordinator below; reads only — mutations
        // serialize on GitRepoLocks and never touch it.
        context.AddSingleton<IGitReadGate, GitReadGate>();
        context.AddSingleton<IRepoActivityTracker, RepoActivityTracker>();
        // One GitService, registered under every capability it implements as well as the whole
        // IGitService. Consumers depend on the narrowest facet they use; the instance is added
        // rather than factory-registered so the fifteen keys can't ever mean fifteen owners.
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
        context.AddService<IGitRepositoryLifecycle>(gitService);
        context.AddService<IGitRawConfigReader>(gitService);
        // Reads config through gitService and back-wires itself into it (its hosted Start) so every
        // git invocation gets the right per-repo name/email/SSH key injected without touching repo
        // config.
        context.AddHostedService<GitIdentityService>();
        context.AddSingleton<DragController>();
        context.AddSingleton<RepoHoverState>();
        context.AddSingleton<RepoBarCollapseState>();
        context.AddSingleton<RepoNodeFactory>();
        context.AddSingleton<LocalChangesSelectionStore>();
        context.AddSingleton<OperationViewModel>();
        // Shared so the Local Changes file list and the workspace-footer merge bar drive the same
        // staging / commit state from either tab.
        context.AddSingleton<LocalChangesViewModel>();
        // The pop-out diff windows: one owner for the app, reached directly by every diff pane's
        // "open in new window", so a pane in a pop-out can open another.
        context.AddSingleton<DiffWindowsViewModel>();

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
                ctx.Require<ISymbolExtractor>(),
                ctx.Require<ISyntaxHighlighter>(),
                ctx.Require<IRepoRegistry>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>(),
                ctx.Require<ILocalizationService>(),
                preferences,
                ctx.Require<LocalChangesViewModel>(),
                ctx.Require<DiffWindowsViewModel>(),
                ctx.Require<IPlatformShell>(),
                subscribeToSelection: false),
            ctx.Require<IRepoRegistry>(),
            ctx.Require<ILocalizationService>()));
        context.AddSingleton<UpdateService>();

        context.AddSingleton<IAppExitGate>(ctx => new UnsavedDocumentsExitGate(
            new AppExitGate(
                ctx.Require<ITerminalSessionStore>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            ctx.Require<IDocumentStore>(),
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>()));

        // Review windows' data seam: the real base..head range source (first-parent, merge-base
        // anchored).
        context.AddSingleton<IReviewStackSource, GitReviewStackSource>();
        // The open review windows, one registry for the app: the windows view reflects it into OS
        // windows and the assistant's review tools point through it at what the reviewer sees.
        context.AddSingleton<ReviewWindowsViewModel>();

        // Review progress (marked-Viewed files) lives for the app session, shared across review
        // windows so closing and reopening a branch's review keeps its progress.
        context.AddSingleton<IReviewProgressStore, ReviewProgressStore>();

        // The terminal pane's two halves: what spawns the shell, and what parses what it writes.
        // Both stateless, and both registered rather than constructed at the pane — this is the only
        // place that names a concrete VT engine, so replacing XtermSharp is a line here.
        context.AddSingleton<IPtySessionFactory, PtySessionFactory>();
        context.AddSingleton<ITerminalEngineFactory, XtermSharpEngineFactory>();
        // What a program in the pane is told when it asks what colour the pane is. Registered
        // beside them because it is the third thing the shell needs from the application and the
        // only one that has to follow the theme.
        context.AddSingleton<ITerminalPalette, ThemeTerminalPalette>();
        // The terminals themselves: one per repository, in memory for the app session, so switching
        // repositories swaps which shell the pane shows rather than retargeting the one it has.
        // Hosted for the same reason the assistant's store is — it watches the registry, which it
        // can only do once the UI loop exists.
        context.AddHostedService<ITerminalSessionStore, TerminalSessionStore>(ctx =>
        {
            var ptys = ctx.Require<IPtySessionFactory>();
            var engines = ctx.Require<ITerminalEngineFactory>();
            var palette = ctx.Require<ITerminalPalette>();
            var clipboard = ctx.Require<IClipboard>();
            return new TerminalSessionStore(
                ctx.Require<IRepoRegistry>(),
                ctx.Require<IUiDispatcher>(),
                repo => new ShellLaunch(repo.Path, ptys, engines, palette, clipboard));
        });

        context.AddSingleton<IFileSystemReader, FileSystemReader>();
        context.AddHostedService<IDocumentStore, DocumentStore>();
        // What keeps the colouring and the fold chevrons describing the buffer rather than the file
        // it was read from: one parse tree per open document, followed into by each edit.
        context.AddHostedService<DocumentAnnotations>();
        context.AddHostedService<RepoDocumentSaver>();
        context.AddSingleton<IUnsavedEditsGuard, UnsavedEditsGuard>();
        context.AddHostedService<IFileBrowserStore, FileBrowserStore>();
        // Registered after the browsers and terminals it follows: it points the content panel at
        // what was opened and keeps the trail the back and forward arrows walk.
        context.AddHostedService<IContentNavigator, ContentNavigator>();
        // Reordering the content panel's tabs. One for the application so its drop line can be
        // drawn at the top of the window; the mounted strip binds its own run in.
        context.AddSingleton<TabDrag>();

        // What a language server is told a file holds: the buffer being typed into where there is
        // one, so a hover answers about what the reader is looking at rather than what was saved.
        context.AddSingleton<IFileTextSource>(ctx => new DocumentBackedText(
            ctx.Require<IDocumentStore>(),
            ctx.Require<IUiDispatcher>()));

        // Costs nothing until the user writes language-servers.json: with no file there is no
        // configuration, so no server is ever launched, nothing is asked of one, and no timer runs.
        // Hosted because it follows the registry, which it can only do once the UI loop exists.
        context.AddHostedService<ILanguageServerStore, LanguageServerStore>();
        context.AddAlias<IWorkspaceSymbolSource, ILanguageServerStore>();

        // Search everywhere: the symbol index is built in the background the first time a
        // repository is active, and the popup reads it along with the running language servers.
        context.AddHostedService<ISymbolIndexStore, SymbolIndexStore>();
        context.AddSingleton<SearchEverywhereViewModel>();

        context.AddHostedService<IRepoSnapshotStore, RepoSnapshotStore>();
        context.AddHostedService<IRepoOperationsStore, RepoOperationsStore>();
        context.AddHostedService<IRepoIndexOperationsStore, RepoIndexOperationsStore>();
        // Samples the read gate + the operations store once a frame into the per-repo "loading" flag
        // the RepoBar rows spin on. Registered after both, and hosted so its frame tick starts with
        // the rest of the app rather than on first row build.
        context.AddHostedService<RepoLoadStore>();
        // The head store owns the checkout; the status store composes its pending branch into
        // RepoStatus and, in return, tells it when a fresh read has landed.
        context.AddSingleton<RepoHeadStore>();
        context.AddAlias<IRepoHeadStore, RepoHeadStore>();
        context.AddAlias<IRepoHeadConfirm, RepoHeadStore>();
        context.AddHostedService<RepoStatusStore>();
        context.AddAlias<IRepoStatusStore, RepoStatusStore>();
        context.AddAlias<IRepoStatusIngest, RepoStatusStore>();
        // Pushes the active repo's id into the read gate, which is what makes the gate admit that
        // repo's reads ahead of the startup sweep instead of behind it.
        context.AddHostedService<GitReadPriorityService>();

        // The assistant's conversations, one per repo, in memory for the app session. The backend is
        // built by the store rather than registered on its own: it needs a live read of the
        // connection the store resolves off the UI thread, which a plain registration would make
        // circular. Which provider that is survives restarts the way the theme and language do.
        context.Bind(
            preferences,
            p => AssistantSettings.From(
                p.AssistantModels.Select(m => (m.Role, m.ProviderId, m.Model)),
                p.AssistantEndpoints.Select(e => (e.ProviderId, (string?)e.BaseUrl))),
            (p, s) => p with
            {
                AssistantModels = s.Models
                    .Select(m => new AssistantModelPreference(AssistantRoles.Id(m.Role), m.Choice.Provider.Id, m.Choice.Model))
                    .ToArray(),
                AssistantEndpoints = s.BaseUrls
                    .Select(e => new AssistantEndpointPreference(e.Key, e.Value))
                    .ToArray(),
            });
        context.AddSingleton(ctx => new AssistantCredentials(ctx.Require<ISecretStore>()));
        context.AddHostedService<IAssistantSessionStore, AssistantSessionStore>(ctx => new AssistantSessionStore(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IGitService>(),
            ctx.Require<ISymbolExtractor>(),
            ctx.Require<AssistantCredentials>(),
            ctx.Require<State<AssistantSettings>>(),
            ctx.Require<ILocalizationService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<LocalChangesViewModel>(),
            ctx.Require<IReviewProgressStore>(),
            ctx.Require<ReviewWindowsViewModel>(),
            ctx.Require<IRepoOperationsStore>(),
            ctx.Require<IDocumentStore>(),
            (_, connection) => new HttpAssistantBackend(AssistantHttp, connection)));
        context.AddSingleton<AssistantPanelPlacement>();
        context.AddSingleton<AppIconImage>();
        context.AddSingleton<AssistantMarkImage>();
        context.AddSingleton<AssistantViewModel>();

        // Local agents over MCP. The preference is one value so the server sees enabled, port and
        // token move together; the state is what the settings card and status bar bind to. The
        // service that keeps the server in step is attached after Build (UseAgentConnections),
        // because the server is the app's. The write surface here is the same hop the assistant's
        // session store builds for itself: a record over shared services, not state of its own.
        var agentConnections = new State<AgentConnectionSettings>(AgentConnectionSettings.From(preferences.Current));
        agentConnections.Changed += s => preferences.Update(p => p.WithAgentConnections(s.Enabled, s.Port, s.Token));
        context.AddService(agentConnections);
        context.AddService(new State<AgentConnectionState>(new AgentConnectionState.Off()));
        context.AddSingleton(ctx => new AssistantWriteSurface(
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<IRepoRegistry>(),
            ctx.Require<LocalChangesViewModel>(),
            ctx.Require<IRepoOperationsStore>(),
            ctx.Require<IDocumentStore>()));
        // Pairing sessions run an agent the app starts itself, pointed at the same server.
        context.AddSingleton(ctx => new AgentEndpoints(
            ctx.Require<State<AgentConnectionSettings>>(),
            ctx.Require<State<AgentConnectionState>>(),
            ctx.Require<IUiDispatcher>()));
        context.AddSingleton(ctx => new PairingSessions(
            ctx.Require<IRepoRegistry>(),
            PairingSessions.Factory(
                ctx.Require<IRepoRegistry>(),
                ctx.Require<IFileBrowserStore>(),
                ctx.Require<IFileTextSource>(),
                ctx.Require<ISymbolExtractor>(),
                ctx.Require<RepoDocumentSaver>(),
                new WorkingTreeSnapshots(ctx.Require<IRepoActivityTracker>()),
                new PairingTestCommands(ctx.Require<PreferencesService>()),
                ctx.Require<AgentEndpoints>(),
                new MapServerEnvironment(LoginShellEnvironment.ForChildProcess),
                ctx.Require<ITerminalSessionStore>(),
                (name, directory, command) => new CommandLaunch(
                    name,
                    directory,
                    command,
                    ctx.Require<IPtySessionFactory>(),
                    ctx.Require<ITerminalEngineFactory>(),
                    ctx.Require<ITerminalPalette>(),
                    ctx.Require<IClipboard>()),
                ctx.Require<IContentNavigator>(),
                ctx.Require<IUiDispatcher>(),
                TimeProvider.System)));
        context.AddSingleton(ctx => new AgentToolMcpSource(
            new AgentToolExport(
                ctx.Require<IGitService>(),
                ctx.Require<ISymbolExtractor>(),
                ctx.Require<IReviewProgressStore>(),
                ctx.Require<ReviewWindowsViewModel>(),
                ctx.Require<AssistantWriteSurface>(),
                ctx.Require<PairingSessions>()),
            ctx.Require<IRepoRegistry>(),
            ctx.Require<ReviewWindowsViewModel>(),
            ctx.Require<AssistantWriteSurface>(),
            TimeProvider.System));

        context.AddHostedService<ToastService>();

        context.AddSingleton(ctx => new PopupTooltipService(ctx.Require<IPopupWindowFactory>()));

        context.AddSingleton(ctx => new HoverPopupService(
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

    // A preference exposed as app-wide observable state: seeded from the stored value, written back
    // on every change.
    private static State<T> Bind<T>(
        this Context context,
        PreferencesService preferences,
        Func<Preferences, T> select,
        Func<Preferences, T, Preferences> apply)
    {
        var state = new State<T>(select(preferences.Current));
        state.Changed += v => preferences.Update(p => apply(p, v));
        context.AddService(state);
        return state;
    }
}
