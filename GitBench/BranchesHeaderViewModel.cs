using ZGF.Observable;

namespace GitBench;

internal sealed class BranchesHeaderViewModel : ViewModelBase<BranchesHeaderState>
{
    public IReadable<string?> BranchName { get; }
    public IReadable<bool> IsDetached { get; }

    public BranchesHeaderViewModel(
        IUiDispatcher dispatcher,
        IRepoStatusStore status)
        : base(dispatcher, BranchesHeaderState.Initial)
    {
        BranchName = Slice(s => s.BranchName);
        IsDetached = Slice(s => s.IsDetached);

        // Pure projection of the active repo's status — no load, no cache. Subscribe fires
        // immediately with the current value, so the header paints without waiting on a query.
        Subscriptions.Add(status.Active.Subscribe(Apply));
    }

    private void Apply(RepoStatus status) =>
        Update(_ => status.IsDetached
            ? new BranchesHeaderState("(detached HEAD)", true)
            : new BranchesHeaderState(status.CurrentBranchName, false));
}

internal sealed record BranchesHeaderState(string? BranchName, bool IsDetached)
{
    public static BranchesHeaderState Initial { get; } = new(null, false);
}
