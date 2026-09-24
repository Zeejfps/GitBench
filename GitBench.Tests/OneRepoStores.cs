using GitBench.Features.FileBrowser;
using GitBench.Features.Terminal;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// One browser behind the store the content panel reads, for tests that build a browser directly
/// and still want the panel's trail over it. Mirrors what <c>FileBrowserStore</c> does with the
/// events: it re-raises its browsers'.
/// </summary>
internal sealed class OneBrowser : IFileBrowserStore, IDisposable
{
    private readonly State<FileBrowserViewModel?> _active;
    private readonly FileBrowserViewModel _browser;

    public OneBrowser(FileBrowserViewModel browser)
    {
        _browser = browser;
        _active = new State<FileBrowserViewModel?>(browser);
        browser.FileShown += Shown;
        browser.AllFilesClosed += Closed;
    }

    public IReadable<FileBrowserViewModel?> Active => _active;

    public FileBrowserViewModel? For(Guid repoId) => _browser;

    public event Action<FileBrowserViewModel, FileBrowserMove>? FileShown;

    public event Action? AllFilesClosed;

    private void Shown(FileBrowserMove move) => FileShown?.Invoke(_browser, move);

    private void Closed() => AllFilesClosed?.Invoke();

    public void Dispose()
    {
        _browser.FileShown -= Shown;
        _browser.AllFilesClosed -= Closed;
        _active.Dispose();
    }
}

/// <summary>A terminal store with no repository in it, for tests about the panel's other tabs.</summary>
internal sealed class NoTerminals : ITerminalSessionStore
{
    public IReadable<TerminalTabs?> Tabs { get; } = new State<TerminalTabs?>(null);

    public bool HasLiveShell(Guid repoId) => false;

    public IReadOnlyList<Guid> ReposWithLiveShells() => [];

    public TerminalInstance StartIn(GitBench.Git.Repo repo, ITerminalLaunch launch) => throw new NotSupportedException();

    public void CloseIn(GitBench.Git.Repo repo, TerminalInstance terminal) { }
}
