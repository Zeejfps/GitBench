using System.Runtime.CompilerServices;
using GitBench.Features.FileBrowser;
using GitBench.Features.Terminal;
using GitBench.Infrastructure;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>Somewhere the content panel can be: one of its tabs, and for a file, where in it.</summary>
/// <remarks>
/// The trail's vocabulary rather than the strip's — <see cref="ContentTab"/> says what a tab is,
/// this says where the reader was. They differ in exactly one place, and it is the point of the
/// type: a file is not only which file, it is the row the tree was on and the line being read.
/// </remarks>
internal abstract record ContentPlace
{
    /// <summary>One of the two views a repository always has: the working changes, or the history.</summary>
    internal sealed record View(MainViewMode Mode) : ContentPlace;

    internal sealed record Shell(TerminalInstance Instance) : ContentPlace;

    internal sealed record File(FileBrowserPlace Place) : ContentPlace;
}

/// <summary>
/// What the content panel is showing, and how the reader got there.
/// </summary>
/// <remarks>
/// Back and forward walk every tab in the strip, not only the files: going from the working changes
/// to the history to a shell to a file and pressing back four times retraces exactly that. So the
/// trail lives here rather than in any one of them — none of those three can see the other two.
/// <para>
/// A trail per repository, because a place names things that belong to one: a terminal instance, a
/// file under a working tree. Switching repositories is therefore not a step — it swaps which trail
/// the arrows walk — and it puts back the tab that repository was left on.
/// </para>
/// </remarks>
internal interface IContentNavigator
{
    IReadable<bool> CanGoBack { get; }

    IReadable<bool> CanGoForward { get; }

    void GoBack();

    void GoForward();

    /// <summary>Goes somewhere, recording where the reader was so they can come back. Asking for the
    /// place already showing is not a step and is not recorded.</summary>
    void Show(ContentPlace place);
}

/// <inheritdoc cref="IContentNavigator"/>
internal sealed class ContentNavigator : IContentNavigator, IHostedService, IDisposable
{
    private readonly IFileBrowserStore _browsers;
    private readonly ITerminalSessionStore _terminals;
    private readonly State<MainViewMode> _mode;

    // Keyed on the browser because that is the per-repository thing this has in hand, and weakly
    // because a repository leaving takes its browser with it: a trail names terminals and files
    // belonging to one working tree, and outliving that tree would be holding both open.
    private readonly ConditionalWeakTable<FileBrowserViewModel, NavigationHistory<ContentPlace>> _trails = new();

    // The tab each repository was left on. Only what the reader chose: a tab handed back because it
    // stopped existing is not recorded, so a repository whose terminals are switched away first
    // does not forget it was on them.
    private readonly ConditionalWeakTable<FileBrowserViewModel, StrongBox<MainViewMode>> _leftOn = new();
    private readonly State<bool> _canGoBack = new(false);
    private readonly State<bool> _canGoForward = new(false);

    private IDisposable? _activeBrowserSub;
    private IDisposable? _activeTabsSub;
    private IDisposable? _activeShellSub;
    private IDisposable? _modeSub;

    private bool _navigating;
    private bool _leaving;
    private bool _started;

    public ContentNavigator(
        IFileBrowserStore browsers,
        ITerminalSessionStore terminals,
        State<MainViewMode> mode)
    {
        _browsers = browsers;
        _terminals = terminals;
        _mode = mode;
    }

    public IReadable<bool> CanGoBack => _canGoBack;

    public IReadable<bool> CanGoForward => _canGoForward;

    public void Start()
    {
        if (_started) return;
        _started = true;

        _browsers.FileShown += OnFileShown;
        _browsers.AllFilesClosed += OnAllFilesClosed;

        _modeSub = _mode.Subscribe(mode =>
        {
            if (!_leaving && Browser is { } browser) _leftOn.GetOrCreateValue(browser).Value = mode;
        });

        // Three things follow a repository switch. The panel goes back to the tab that repository
        // was left on. A repository with nothing open cannot show a file, nor one without a shell a
        // terminal, so either would leave the panel on a tab the strip does not have. And the
        // arrows read that repository's trail, so they have to be re-read.
        _activeBrowserSub = _browsers.Active.Subscribe(browser =>
        {
            if (browser is not null && _leftOn.TryGetValue(browser, out var left)) _mode.Value = left.Value;
            if (browser?.ActiveTab.Value is null) LeaveIf(MainViewMode.Files);
            if (Shells?.Active.Value is null) LeaveIf(MainViewMode.Terminal);
            Update();
        });

        // The same question asked of the terminals, whose answer is one observable rather than an
        // event: the active terminal is null exactly while the repository has no terminal tab.
        _activeTabsSub = _terminals.Tabs.Subscribe(tabs =>
        {
            _activeShellSub?.Dispose();
            _activeShellSub = tabs?.Active.Subscribe(shell =>
            {
                if (shell is null) LeaveIf(MainViewMode.Terminal);
            });
            if (tabs is null) LeaveIf(MainViewMode.Terminal);
        });
    }

    public void Show(ContentPlace place)
    {
        var from = Here();
        if (from is not null && !from.Equals(place)) Push(from);
        Apply(place);
        Update();
    }

    public void GoBack() => Step(back: true);

    public void GoForward() => Step(back: false);

    /// <summary>
    /// Walks one way until something applies. A place whose tab has gone — a terminal that was
    /// closed, a repository that was dropped — is passed over rather than being a press that does
    /// nothing, and the place being left is handed over only once however many are stepped past.
    /// </summary>
    private void Step(bool back)
    {
        if (Trail is not { } trail) return;

        var leaving = Here();
        while (back ? trail.TryGoBack(leaving, out var place) : trail.TryGoForward(leaving, out place))
        {
            leaving = null;
            if (Apply(place)) break;
        }

        Update();
    }

    /// <summary>Where the reader is now. Null when the tab on screen is not a place the trail can
    /// name — a file pane with nothing open, a terminal tab in a repository with no terminals.</summary>
    private ContentPlace? Here() => _mode.Value switch
    {
        MainViewMode.LocalChanges or MainViewMode.History => new ContentPlace.View(_mode.Value),
        MainViewMode.Terminal => Shells?.Active.Value is { } shell ? new ContentPlace.Shell(shell) : null,
        MainViewMode.Files => Browser?.Place is { } place ? new ContentPlace.File(place) : null,
        _ => null,
    };

    /// <summary>Puts a place on screen, and answers whether it is still somewhere that exists.</summary>
    private bool Apply(ContentPlace place)
    {
        _navigating = true;
        try
        {
            switch (place)
            {
                case ContentPlace.View view:
                    _mode.Value = view.Mode;
                    return true;

                case ContentPlace.Shell shell when Shells is { } shells
                    && shells.Terminals.IndexOf(shell.Instance) >= 0:
                    shells.Activate(shell.Instance);
                    _mode.Value = MainViewMode.Terminal;
                    return true;

                case ContentPlace.File file when Browser is { } browser:
                    // Reopens the tab when it has been closed since, which is what coming back to a
                    // file means; the browser's own restore puts the tree and the line back too.
                    browser.Restore(file.Place);
                    _mode.Value = MainViewMode.Files;
                    return true;

                default:
                    return false;
            }
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>
    /// A browser moved. Recorded here rather than by the browser, because where the reader was
    /// may not have been a file at all — opening one from the tree while the working changes are on
    /// screen is a step away from the working changes. A repository not on screen is left alone and
    /// comes back on its files, where the move went.
    /// </summary>
    private void OnFileShown(FileBrowserViewModel browser, FileBrowserMove move)
    {
        if (!ReferenceEquals(browser, Browser))
        {
            _leftOn.GetOrCreateValue(browser).Value = MainViewMode.Files;
            return;
        }

        if (!_navigating)
        {
            var onFiles = _mode.Value == MainViewMode.Files;
            var from = onFiles
                ? move.From is { } place ? new ContentPlace.File(place) : null
                : Here();

            // Coming from another tab is always a step. Staying in the files is one only when the
            // browser actually went somewhere.
            if (from is not null && (!onFiles || move.IsMove)) Push(from);
        }

        _mode.Value = MainViewMode.Files;
        Update();
    }

    private void OnAllFilesClosed() => LeaveIf(MainViewMode.Files);

    /// <summary>Hands the panel back to the working changes when the tab it was on stops existing.
    /// Not a step: nobody navigated, the place simply went.</summary>
    private void LeaveIf(MainViewMode showing)
    {
        if (_mode.Value != showing) return;

        _leaving = true;
        try
        {
            _mode.Value = MainViewMode.LocalChanges;
        }
        finally
        {
            _leaving = false;
        }
    }

    private void Push(ContentPlace place)
    {
        Trail?.Push(place);
        Update();
    }

    private void Update()
    {
        _canGoBack.Value = Trail?.CanGoBack ?? false;
        _canGoForward.Value = Trail?.CanGoForward ?? false;
    }

    private FileBrowserViewModel? Browser => _browsers.Active.Value;

    private TerminalTabs? Shells => _terminals.Tabs.Value;

    private NavigationHistory<ContentPlace>? Trail =>
        Browser is { } browser ? _trails.GetValue(browser, _ => new NavigationHistory<ContentPlace>()) : null;

    public void Dispose()
    {
        _browsers.FileShown -= OnFileShown;
        _browsers.AllFilesClosed -= OnAllFilesClosed;
        _modeSub?.Dispose();
        _activeBrowserSub?.Dispose();
        _activeShellSub?.Dispose();
        _activeTabsSub?.Dispose();
        _canGoBack.Dispose();
        _canGoForward.Dispose();
    }
}
