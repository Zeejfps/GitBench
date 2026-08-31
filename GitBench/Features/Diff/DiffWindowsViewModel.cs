using GitBench.Features.CodeIntel;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Diff;

// Owns the set of open pop-out diff windows as observable state. Subscribes to
// OpenDiffWindowMessage and, for each request, spins up an independent DiffViewModel pinned
// to that target — so the window stays locked to its file regardless of the main window's
// selection, while still refreshing live on working-tree changes and supporting hunk- and
// file-level staging. DiffWindowsPresenter reflects this list into real OS windows.
internal sealed class DiffWindowsViewModel : IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitDiffReader _gitDiff;
    private readonly IGitWorkingTreeOperations _gitWorkingTree;
    private readonly IGitConflictOperations _gitConflicts;
    private readonly ISymbolExtractor _extractor;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly IDisposable _subscription;

    public ObservableList<DiffWindowViewModel> Windows { get; } = new();

    public DiffWindowsViewModel(
        IRepoRegistry registry,
        IGitDiffReader gitDiff,
        IGitWorkingTreeOperations gitWorkingTree,
        IGitConflictOperations gitConflicts,
        ISymbolExtractor extractor,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc)
    {
        _registry = registry;
        _gitDiff = gitDiff;
        _gitWorkingTree = gitWorkingTree;
        _gitConflicts = gitConflicts;
        _extractor = extractor;
        _dispatcher = dispatcher;
        _bus = bus;
        _loc = loc;
        _subscription = _bus.SubscribeScoped<OpenDiffWindowMessage>(OnOpenRequested);
    }

    private void OnOpenRequested(OpenDiffWindowMessage m)
    {
        // A fixed, never-mutated target observable: the main window's selection cannot change
        // what this window shows. The DiffViewModel still reloads on WorkingTreeChangedMessage.
        var pinned = new State<DiffTarget?>(m.Target);
        var diff = new DiffViewModel(pinned, _registry, _gitDiff, _gitWorkingTree, _gitConflicts, _dispatcher, _bus, _extractor, loc: _loc, pinnedRepoId: m.RepoId);
        Windows.Add(new DiffWindowViewModel(m.Target.Path, diff));
    }

    // Removes a window (e.g. the user clicked the native close button). The removal drives the
    // presenter to tear down the OS window; we then dispose the window's view model.
    public void Close(DiffWindowViewModel window)
    {
        if (Windows.Remove(window))
            window.Dispose();
    }

    public void Dispose()
    {
        _subscription.Dispose();
        foreach (var w in Windows) w.Dispose();
        Windows.Clear();
    }
}
