using GitBench.Features.Repos;
using GitBench.Infrastructure;
using GitBench.Localization;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class BranchesHeaderViewModel : ViewModelBase<BranchesHeaderState>
{
    private readonly ILocalizationService _loc;
    private RepoStatus? _lastStatus;

    public IReadable<string?> BranchName { get; }
    public IReadable<bool> IsDetached { get; }

    public BranchesHeaderViewModel(
        IUiDispatcher dispatcher,
        IRepoStatusStore status,
        ILocalizationService loc)
        : base(dispatcher, BranchesHeaderState.Initial)
    {
        _loc = loc;
        BranchName = Slice(s => s.BranchName);
        IsDetached = Slice(s => s.IsDetached);

        // Pure projection of the active repo's status — no load, no cache. Subscribe fires
        // immediately with the current value, so the header paints without waiting on a query.
        Subscriptions.Add(status.Active.Subscribe(Apply));
        // The detached-HEAD placeholder is localized, so re-project it on a live locale switch.
        Subscriptions.Add(_loc.Strings.Subscribe(_ => { if (_lastStatus is { } last) Apply(last); }));
    }

    private void Apply(RepoStatus status)
    {
        _lastStatus = status;
        Update(_ => status.IsDetached
            ? new BranchesHeaderState(_loc.Strings.Value.BranchesHeaderDetached, true)
            : new BranchesHeaderState(status.CurrentBranchName, false));
    }
}

internal sealed record BranchesHeaderState(string? BranchName, bool IsDetached)
{
    public static BranchesHeaderState Initial { get; } = new(null, false);
}
