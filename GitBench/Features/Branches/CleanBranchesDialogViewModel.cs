using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

// Why a local branch is offered for cleanup, mirrored from BranchUpstreamState:
// Disconnected = upstream was set but the remote ref is gone; NeverPushed = no upstream.
internal enum BranchCleanupKind { Disconnected, NeverPushed }

internal readonly record struct CleanBranchCandidate(string Name, BranchCleanupKind Kind);

internal sealed class CleanBranchesDialogViewModel : IDialogViewModel
{
    private readonly Repo _repo;
    private readonly IReadOnlyList<CleanBranchCandidate> _candidates;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;

    // Carries per-branch failures out of the background delete loop into the UI-thread success
    // callback — same single-writer-before-read pattern as DeleteLocalBranchDialogViewModel.
    private List<string>? _failures;

    public int DisconnectedCount { get; }
    public int NeverPushedCount { get; }

    public State<bool> CleanDisconnected { get; }
    public State<bool> CleanNeverPushed { get; } = new(false);
    public State<bool> Force { get; } = new(true);

    // Branch names the user has individually unchecked while their category is still enabled.
    private readonly State<IReadOnlySet<string>> _unchecked = new(new HashSet<string>());

    private readonly Derived<IReadOnlyList<CleanBranchCandidate>> _visibleCandidates;
    private readonly Derived<IReadOnlyList<string>> _selectedNames;
    private readonly Derived<string> _selectedHeader;
    private readonly Derived<string> _actionLabel;
    private readonly Derived<bool> _canClean;

    // The rows shown in the dialog — candidates whose category is enabled. Kept independent of the
    // per-branch unchecks so toggling one branch doesn't rebuild (and re-seed) the whole list.
    public IReadable<IReadOnlyList<CleanBranchCandidate>> VisibleCandidates => _visibleCandidates;
    public IReadable<string> SelectedHeader => _selectedHeader;
    public IReadable<string> ActionLabel => _actionLabel;

    public AsyncCommand Clean { get; }

    public event Action? CloseRequested;

    public CleanBranchesDialogViewModel(
        Repo repo,
        IReadOnlyList<CleanBranchCandidate> candidates,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc)
    {
        _repo = repo;
        _candidates = candidates;
        _gitService = gitService;
        _bus = bus;
        _loc = loc;

        DisconnectedCount = candidates.Count(c => c.Kind == BranchCleanupKind.Disconnected);
        NeverPushedCount = candidates.Count(c => c.Kind == BranchCleanupKind.NeverPushed);

        // Pre-select the safer set: disconnected branches were usually merged on a now-deleted
        // PR, so default them on; never-pushed branches may be only-local work, so default off.
        CleanDisconnected = new State<bool>(DisconnectedCount > 0);

        _visibleCandidates = new Derived<IReadOnlyList<CleanBranchCandidate>>(() =>
            _candidates.Where(c => IsKindSelected(c.Kind)).ToList());

        _selectedNames = new Derived<IReadOnlyList<string>>(() =>
        {
            var excluded = _unchecked.Value;
            return _visibleCandidates.Value
                .Where(c => !excluded.Contains(c.Name))
                .Select(c => c.Name)
                .ToList();
        });

        _selectedHeader = new Derived<string>(() =>
        {
            var count = _selectedNames.Value.Count;
            return count == 0
                ? _loc.Strings.Value.BranchesCleanNoneSelected
                : _loc.Strings.Value.BranchesCleanSelectedHeader(count);
        });

        _actionLabel = new Derived<string>(() =>
            _loc.Strings.Value.BranchesCleanAction(_selectedNames.Value.Count));

        _canClean = new Derived<bool>(() => _selectedNames.Value.Count > 0);
        Clean = new AsyncCommand(dispatcher, DoClean, OnCleanSucceeded, _canClean);
    }

    private bool IsKindSelected(BranchCleanupKind kind) => kind switch
    {
        BranchCleanupKind.Disconnected => CleanDisconnected.Value,
        BranchCleanupKind.NeverPushed => CleanNeverPushed.Value,
        _ => false,
    };

    public bool IsBranchChecked(string name) => !_unchecked.Value.Contains(name);

    public void ToggleBranch(string name)
    {
        var next = new HashSet<string>(_unchecked.Value);
        if (!next.Add(name)) next.Remove(name);
        _unchecked.Value = next;
    }

    private bool IsSelected(CleanBranchCandidate c) => IsKindSelected(c.Kind) && !_unchecked.Value.Contains(c.Name);

    private string? DoClean()
    {
        var force = Force.Value;
        var failures = new List<string>();
        var anySuccess = false;

        foreach (var c in _candidates)
        {
            if (!IsSelected(c)) continue;
            GitOutcome outcome;
            try { outcome = _gitService.DeleteBranch(_repo, c.Name, force); }
            catch (Exception ex) { outcome = new GitOutcome.Failed(ex.Message); }
            if (outcome is GitOutcome.Failed failed) failures.Add($"{c.Name}: {failed.Message}");
            else anySuccess = true;
        }

        // Nothing deleted (e.g. Force off and none merged): surface inline and keep the dialog
        // open so the user can enable Force and retry. Otherwise close and report the stragglers.
        if (!anySuccess && failures.Count > 0)
            return string.Join("\n", failures);

        _failures = failures.Count > 0 ? failures : null;
        return null;
    }

    private void OnCleanSucceeded()
    {
        _bus.Broadcast(new RefsChangedMessage(_repo.Id));
        CloseRequested?.Invoke();
        if (_failures is { } failures)
            _bus.Broadcast(new ShowOperationErrorMessage(
                _loc.Strings.Value.BranchesCleanErrorTitle,
                string.Join("\n", failures)));
    }

    public void Dispose()
    {
        (Clean.CanExecute as IDisposable)?.Dispose();
        _canClean.Dispose();
        _actionLabel.Dispose();
        _selectedHeader.Dispose();
        _selectedNames.Dispose();
        _visibleCandidates.Dispose();
        _unchecked.Dispose();
        CleanDisconnected.Dispose();
        CleanNeverPushed.Dispose();
        Force.Dispose();
    }
}
