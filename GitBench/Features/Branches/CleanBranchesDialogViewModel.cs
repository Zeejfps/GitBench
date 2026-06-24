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

    private readonly Derived<IReadOnlyList<string>> _selectedNames;
    private readonly Derived<string> _selectedHeader;
    private readonly Derived<string> _actionLabel;
    private readonly Derived<bool> _canClean;

    public IReadable<IReadOnlyList<string>> SelectedNames => _selectedNames;
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

        _selectedNames = new Derived<IReadOnlyList<string>>(() =>
            _candidates.Where(c => IsKindSelected(c.Kind)).Select(c => c.Name).ToList());

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

    private string? DoClean()
    {
        var force = Force.Value;
        var failures = new List<string>();
        var anySuccess = false;

        foreach (var c in _candidates)
        {
            if (!IsKindSelected(c.Kind)) continue;
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
        CleanDisconnected.Dispose();
        CleanNeverPushed.Dispose();
        Force.Dispose();
    }
}
