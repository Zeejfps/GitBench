using GitBench.Features.CodeIntel;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Observable;

namespace GitBench.Features.Diff;

// Owns the set of open pop-out diff windows as observable state. Open spins up an independent
// DiffViewModel pinned to the requested target — so the window stays locked to its file
// regardless of the main window's selection, while still refreshing live on working-tree changes
// and supporting hunk- and file-level staging. DiffWindowsView reflects this list into real OS
// windows.
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
    private readonly LocalChangesViewModel _localChanges;
    private readonly IPlatformShell _shell;

    public ObservableList<DiffWindowViewModel> Windows { get; } = new();

    public DiffWindowsViewModel(
        IRepoRegistry registry,
        IGitDiffReader gitDiff,
        IGitWorkingTreeOperations gitWorkingTree,
        IGitConflictOperations gitConflicts,
        ISymbolExtractor extractor,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        LocalChangesViewModel localChanges,
        IPlatformShell shell)
    {
        _registry = registry;
        _gitDiff = gitDiff;
        _gitWorkingTree = gitWorkingTree;
        _gitConflicts = gitConflicts;
        _extractor = extractor;
        _dispatcher = dispatcher;
        _bus = bus;
        _loc = loc;
        _localChanges = localChanges;
        _shell = shell;
    }

    // Target is the pinned path/side/sha — not a frozen result — so the window's own DiffViewModel
    // loads and stays live for that file. repoId is the source pane's pinned repo.
    public void Open(DiffTarget target, Guid repoId)
    {
        // A fixed, never-mutated target observable: the main window's selection cannot change
        // what this window shows. The DiffViewModel still reloads on WorkingTreeChangedMessage.
        var pinned = new State<DiffTarget?>(target);
        var diff = new DiffViewModel(pinned, _registry, _gitDiff, _gitWorkingTree, _gitConflicts, _dispatcher, _bus, _extractor, _loc, _localChanges, this, _shell, repoId);
        Windows.Add(new DiffWindowViewModel(target.Path, diff));
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
        foreach (var w in Windows) w.Dispose();
        Windows.Clear();
    }
}
