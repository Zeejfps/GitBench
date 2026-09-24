using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>The one place the file browsers live: one per repository, for the app session.</summary>
internal interface IFileBrowserStore
{
    /// <summary>The active repository's browser, or null when no repository is active. Swaps on repo
    /// switch, so the pane binds to this and never asks which repo it is showing.</summary>
    IReadable<FileBrowserViewModel?> Active { get; }

    /// <summary>A repository's browser, whether or not it is the one on screen; null for a
    /// repository that is not open.</summary>
    FileBrowserViewModel? For(Guid repoId);

    /// <summary>A file became the one a browser is showing — the tree was clicked, a definition was
    /// jumped to, the back button was pressed, or something moved a browser that is not on screen.</summary>
    event Action<FileBrowserViewModel, FileBrowserMove>? FileShown;

    /// <summary>The active browser's last open file was closed.</summary>
    event Action? AllFilesClosed;
}

/// <summary>
/// Owns every repository's file browser, keyed by repo id, so switching away and back returns to the
/// same open directories and the same cursor — and so does relaunching the app, because unlike a
/// shell a set of open directories is worth keeping.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="Terminal.TerminalSessionStore"/>'s shape: per-repo state, an "active"
/// projection that swaps on repo switch, entries dropped when a repository leaves the registry, and
/// a <see cref="Start"/> that wires the registry once the UI loop exists.
/// </para>
/// <para>
/// It also listens for the working tree moving. An index-only change is skipped outright — staging a
/// hunk broadcasts one and nothing on disk moved — and what remains is answered by re-listing the
/// open directories rather than by dropping them, because the reconcile service broadcasts one of
/// these every thirty seconds per active repository whether anything happened or not.
/// </para>
/// </remarks>
internal sealed class FileBrowserStore : IFileBrowserStore, IHostedService, IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitRepositoryReader _git;
    private readonly IFileSystemReader _files;
    private readonly ISymbolExtractor _extractor;
    private readonly ISyntaxHighlighter _highlighter;
    private readonly IMessageBus _bus;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDocumentStore _documents;

    private readonly Dictionary<Guid, FileBrowserViewModel> _browsers = new();
    private readonly Dictionary<FileBrowserViewModel, Relay> _relays = new();
    private readonly State<FileBrowserViewModel?> _active = new(null);

    private IDisposable? _activeSub;
    private IDisposable? _reposSub;
    private IDisposable? _workingTreeSub;
    private bool _started;
    private bool _disposed;

    public FileBrowserStore(
        IRepoRegistry registry,
        IGitRepositoryReader git,
        IFileSystemReader files,
        ISymbolExtractor extractor,
        ISyntaxHighlighter highlighter,
        IMessageBus bus,
        IUiDispatcher dispatcher,
        IDocumentStore documents)
    {
        _registry = registry;
        _git = git;
        _files = files;
        _extractor = extractor;
        _highlighter = highlighter;
        _bus = bus;
        _dispatcher = dispatcher;
        _documents = documents;
    }

    public IReadable<FileBrowserViewModel?> Active => _active;

    public event Action<FileBrowserViewModel, FileBrowserMove>? FileShown;

    public event Action? AllFilesClosed;

    public void Start()
    {
        if (_started) return;
        _started = true;

        _activeSub = _registry.Active.Subscribe(_ => OnActiveChanged());
        _reposSub = _registry.Repos.Subscribe(_ => DropClosedRepos());
        _workingTreeSub = _bus.SubscribeScoped<WorkingTreeChangedMessage>(OnWorkingTreeChanged);
    }

    public FileBrowserViewModel? For(Guid repoId)
    {
        if (_disposed) return null;
        return _registry.Repos.FirstOrDefault(repo => repo.Id == repoId) is { } open ? BrowserFor(open) : null;
    }

    private void OnActiveChanged()
    {
        if (_disposed) return;
        _active.Value = _registry.Active.Value is { } repo ? BrowserFor(repo) : null;
    }

    private void OnWorkingTreeChanged(WorkingTreeChangedMessage message)
    {
        if (_disposed) return;
        if (message.IndexOnly) return;
        if (_browsers.TryGetValue(message.RepoId, out var browser)) browser.Invalidate();
    }

    private FileBrowserViewModel BrowserFor(Repo repo)
    {
        if (_browsers.TryGetValue(repo.Id, out var existing)) return existing;

        var repoId = repo.Id;
        var browser = new FileBrowserViewModel(
            repo,
            _files,
            paths => _git.IsPathIgnored(repo, paths),
            () => _git.ListWorkingTreeFiles(repo),
            _extractor,
            _highlighter,
            _dispatcher,
            _registry.GetFileBrowserUi(repoId),
            state => _registry.SetFileBrowserUi(repoId, state),
            _documents.For(repoId),
            AskBeforeDiscarding,
            AskBeforeReloading);
        var relay = new Relay(move => FileShown?.Invoke(browser, move), () => RaiseAllFilesClosed(browser));
        browser.FileShown += relay.Shown;
        browser.AllFilesClosed += relay.Closed;
        _relays[browser] = relay;
        _browsers[repo.Id] = browser;
        return browser;
    }

    // A background browser emptying says nothing about the tab on screen: coming back to it is
    // what finds it empty.
    private void RaiseAllFilesClosed(FileBrowserViewModel browser)
    {
        if (ReferenceEquals(_active.Value, browser)) AllFilesClosed?.Invoke();
    }

    /// <summary>Puts the "these edits are not on disk" question on screen, and runs the close only
    /// if it is answered.</summary>
    private void AskBeforeDiscarding(IReadOnlyList<string> files, Action close) =>
        _bus.Broadcast(new ShowDialogMessage(onClose => new UnsavedChangesDialog
        {
            Files = files,
            OnClose = onClose,
            OnConfirm = close,
        }));

    /// <summary>Puts the "these files moved on disk under what you typed" question on screen, and
    /// runs the reload only if it is chosen.</summary>
    private void AskBeforeReloading(IReadOnlyList<string> files, Action reload) =>
        _bus.Broadcast(new ShowDialogMessage(onClose => new UnsavedChangesDialog
        {
            Files = files,
            OnClose = onClose,
            OnConfirm = reload,
            Kind = UnsavedChangesKind.ChangedOnDisk,
        }));

    private void DropClosedRepos()
    {
        if (_disposed || _browsers.Count == 0) return;

        var open = _registry.Repos.Select(r => r.Id).ToHashSet();
        foreach (var id in _browsers.Keys.Where(id => !open.Contains(id)).ToArray())
        {
            var browser = _browsers[id];
            _browsers.Remove(id);
            if (ReferenceEquals(_active.Value, browser)) _active.Value = null;
            Release(browser);
        }
    }

    private void Release(FileBrowserViewModel browser)
    {
        if (_relays.Remove(browser, out var relay))
        {
            browser.FileShown -= relay.Shown;
            browser.AllFilesClosed -= relay.Closed;
        }

        browser.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _activeSub?.Dispose();
        _reposSub?.Dispose();
        _workingTreeSub?.Dispose();

        _active.Value = null;

        foreach (var browser in _browsers.Values) Release(browser);
        _browsers.Clear();
        _active.Dispose();
    }

    /// <summary>What a browser's events are passed on through, kept so they can be taken off again.</summary>
    private sealed record Relay(Action<FileBrowserMove> Shown, Action Closed);
}
