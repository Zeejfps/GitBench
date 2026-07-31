using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// Where HEAD is going on one repo. PendingBranch stands from the moment a checkout starts until a
// fresh read confirms where HEAD landed — which is later than the git command returning, because
// every reader downstream still holds the pre-checkout name until that read lands.
public sealed record RepoHead(string? PendingBranch, bool IsCheckoutRunning)
{
    public static readonly RepoHead Settled = new(null, false);
}

// Single source of truth for an in-flight branch switch, keyed per repo. It owns the checkout itself
// so the pending name and the operation can't drift apart: everything that switches branches goes
// through Checkout, and everything that wants to know where HEAD is reads IRepoStatusStore, which
// composes this in.
public interface IRepoHeadStore
{
    // This repo's HEAD motion. Call inside a reactive binding — the read is auto-tracked.
    RepoHead For(Guid repoId);

    // Switches the repo to branchName. A second call while one is already running is dropped.
    void Checkout(Repo repo, string branchName);
}

// The write side: a fresh observation of this repo's HEAD landed, from whichever read produced it.
// Deliberately not a member of IRepoHeadStore — that's the read seam the view models hold, and none
// of them settles a checkout.
internal interface IRepoHeadConfirm
{
    void Confirm(Guid repoId);
}

/// <summary>
/// Owns the checkout lifecycle and the pending-HEAD name per repo. Mirrors
/// <see cref="RepoOperationsStore"/>'s shape — per-repo state, work off-thread, results posted back —
/// but for the one local operation whose in-flight target every other component needs. While a
/// checkout runs, the branch listing and the status probe both still name the <em>old</em> branch, so
/// anything that seeds a git argument from them (create branch, publish, review, merge) aims at the
/// branch the user just left. Holding the pending name here, and composing it into
/// <see cref="RepoStatus"/>, is what gives those callers one answer.
///
/// The pending name deliberately outlives the git command: it clears on the first fresh HEAD
/// observation <em>after</em> the command finished, not when the command returns, because that later
/// moment is when the rest of the app stops being stale.
/// </summary>
internal sealed class RepoHeadStore : IRepoHeadStore, IRepoHeadConfirm, IDisposable
{
    private readonly IGitService _git;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    // Per-repo source of truth, created lazily on first touch. UI-thread only — no locking needed.
    private readonly Dictionary<Guid, State<RepoHead>> _states = new();

    public RepoHeadStore(IGitService git, IMessageBus bus, ILocalizationService loc, IUiDispatcher dispatcher)
    {
        _git = git;
        _bus = bus;
        _loc = loc;
        _dispatcher = dispatcher;
    }

    public RepoHead For(Guid repoId) => Get(repoId).Value;

    public void Checkout(Repo repo, string branchName)
    {
        var s = Get(repo.Id);
        if (s.Value.IsCheckoutRunning) return;
        s.Value = new RepoHead(branchName, IsCheckoutRunning: true);

        var dispatcher = _dispatcher;
        Task.Run(() =>
        {
            GitOutcome outcome;
            try { outcome = _git.CheckoutLocalBranch(repo, branchName); }
            catch (Exception ex) { outcome = new GitOutcome.Failed(ex.Message); }
            dispatcher.Post(() => Complete(repo, outcome));
        });
    }

    // Keyed on the captured repo, so the result lands on that repo's slot no matter which repo is
    // active when it finishes.
    private void Complete(Repo repo, GitOutcome outcome)
    {
        if (_disposed) return;
        var s = Get(repo.Id);
        var failed = outcome as GitOutcome.Failed;

        // A failure never moved HEAD, so drop the pending name now rather than waiting for a read to
        // disagree with it — leaving it standing would keep every caller aimed at a branch we're not on.
        s.Value = failed == null ? s.Value with { IsCheckoutRunning = false } : RepoHead.Settled;

        _bus.Broadcast(new RefsChangedMessage(repo.Id));
        _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
        if (failed != null)
            _bus.Broadcast(new ShowOperationErrorMessage(_loc.Strings.Value.BranchesErrorCheckoutFailed, failed.Message));
    }

    // Called whenever a fresh status read is accepted into a repo's slot. A read is only accepted if
    // it's the newest one, and the RefsChangedMessage above kicks a newer read the instant the command
    // finishes — so any accepted read arriving once IsCheckoutRunning has cleared observed the
    // post-checkout HEAD, whatever it found. Settling on that rather than on a name match is what
    // stops a pending name outliving a checkout that landed somewhere unexpected.
    public void Confirm(Guid repoId)
    {
        if (_disposed) return;
        if (!_states.TryGetValue(repoId, out var s)) return;
        var cur = s.Value;
        if (cur.PendingBranch == null || cur.IsCheckoutRunning) return;
        s.Value = RepoHead.Settled;
    }

    private State<RepoHead> Get(Guid id)
    {
        if (!_states.TryGetValue(id, out var s))
        {
            s = new State<RepoHead>(RepoHead.Settled);
            _states[id] = s;
        }
        return s;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var s in _states.Values) s.Dispose();
        _states.Clear();
    }
}
