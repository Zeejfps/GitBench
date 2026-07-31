using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Infrastructure;
using GitBench.Localization;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class BranchesHeaderViewModel : ViewModelBase<BranchesHeaderState>
{
    private readonly ILocalizationService _loc;
    private readonly SpinnerAnimation _spinner;
    private RepoStatus? _lastStatus;

    public IReadable<string?> BranchName { get; }
    public IReadable<bool> IsDetached { get; }
    public IReadable<bool> IsSwitching { get; }

    /// <summary>Angle for the switching spinner; bind the header's glyph rotation to it.</summary>
    public IReadable<float> SwitchRotation => _spinner.Rotation;

    public BranchesHeaderViewModel(
        IUiDispatcher dispatcher,
        IRepoStatusStore status,
        IFrameTicker ticker,
        ILocalizationService loc)
        : base(dispatcher, BranchesHeaderState.Initial)
    {
        _loc = loc;
        _spinner = new SpinnerAnimation(ticker);
        BranchName = Slice(s => s.BranchName);
        IsDetached = Slice(s => s.IsDetached);
        IsSwitching = Slice(s => s.IsSwitching);

        // Pure projection of the active repo's status — no load, no cache. Subscribe fires
        // immediately with the current value, so the header paints without waiting on a query.
        Subscriptions.Add(status.Active.Subscribe(Apply));
        // The detached-HEAD placeholder is localized, so re-project it on a live locale switch.
        Subscriptions.Add(_loc.Strings.Subscribe(_ => { if (_lastStatus is { } last) Apply(last); }));
    }

    private void Apply(RepoStatus status)
    {
        _lastStatus = status;
        // A pending name wins: this header is the most prominent claim in the app about which branch
        // you're on, so during a switch it names the destination and says it's still moving rather
        // than confidently showing the branch you just left.
        Update(_ => status.PendingBranchName is { } pending
            ? new BranchesHeaderState(pending, false, true)
            : status.IsDetached
                ? new BranchesHeaderState(_loc.Strings.Value.BranchesHeaderDetached, true, false)
                : new BranchesHeaderState(status.CurrentBranchName, false, false));
        if (status.IsHeadInMotion) _spinner.Start();
        else _spinner.Stop();
    }

    public override void Dispose()
    {
        _spinner.Dispose();
        base.Dispose();
    }
}

internal sealed record BranchesHeaderState(string? BranchName, bool IsDetached, bool IsSwitching)
{
    public static BranchesHeaderState Initial { get; } = new(null, false, false);
}
