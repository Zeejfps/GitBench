using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// Where HEAD is going on one repo. PendingBranch stands from the moment a HEAD-moving command starts
// until a fresh read confirms where HEAD landed — which is later than the command returning, because
// every reader downstream still holds the old name until that read lands. IsMoving is narrower: it
// covers only the command itself.
public sealed record RepoHead(string? PendingBranch, bool IsMoving)
{
    public static readonly RepoHead Settled = new(null, false);
}

// Single source of truth for an in-flight branch switch, keyed per repo. Every operation that moves
// HEAD goes through Checkout (which owns the command) or BeginMove (which brackets one the caller
// runs), so the pending name and the operation can't drift apart — and everything that wants to know
// where HEAD is reads IRepoStatusStore, which composes this in.
public interface IRepoHeadStore
{
    // This repo's HEAD motion. Call inside a reactive binding — the read is auto-tracked.
    RepoHead For(Guid repoId);

    // Switches the repo to branchName. A second call while one is already running is dropped.
    void Checkout(Repo repo, string branchName);

    // Runs a command that leaves HEAD on branchName, start to finish: declares the destination,
    // runs it off-thread, settles, broadcasts the refresh, and reports a failure under failureTitle
    // (defaulting to the checkout-failed wording). Prefer this over BeginMove wherever the caller
    // has no per-op state to update: a view model's background continuations are dropped when it is
    // disposed, so settling from one would leave the declaration standing forever if the user
    // switched repo mid-command — wedging every later checkout shut.
    void RunMove(Repo repo, string branchName, Func<GitOutcome> work, string? failureTitle = null);

    // Declares that a command the caller runs itself will leave HEAD on branchName, and returns the
    // callback that ends that declaration — invoke it on the UI thread, from a path that runs
    // whatever happens, with whether the command succeeded. Handing the settle back as the return
    // value is what keeps the two halves paired: there is no way to begin a move without holding the
    // thing that ends it, and each settle ends only its own declaration, so overlapping moves can't
    // cancel each other.
    //
    // Every operation that moves HEAD goes through this or RunMove — create-branch-with-checkout,
    // remote checkout, reset-branch-and-switch, attaching a detached HEAD, renaming the current
    // branch. Each of them used to move HEAD silently, leaving every reader holding the old name.
    Action<bool> BeginMove(Repo repo, string branchName);
}

// The write side: a fresh observation of this repo's HEAD landed, from whichever read produced it.
// Deliberately not a member of IRepoHeadStore — that's the read seam the view models hold, and none
// of them settles a checkout.
internal interface IRepoHeadConfirm
{
    void Confirm(Guid repoId);
}

/// <summary>
/// Owns the pending-HEAD name per repo, and the checkout that is the plain way to move it. Mirrors
/// <see cref="RepoOperationsStore"/>'s shape — per-repo state, work off-thread, results posted back.
/// While HEAD is moving, the branch listing and the status probe both still name the <em>old</em>
/// branch, so anything that seeds a git argument from them (create branch, publish, review, merge)
/// aims at the branch the user just left. Holding the pending name here, and composing it into
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

    // The declarations currently open on each repo, oldest first, and the destination of the last
    // one that actually landed. Kept as a list rather than a single slot because a settle must only
    // ever end its own declaration: with one slot, a second declaration overwrites the first, and
    // then whichever settles first clears a move that is still running — leaving every reader back
    // on the stale probed name while git is mid-switch. UI-thread only.
    private readonly Dictionary<Guid, MoveLog> _moves = new();
    private int _nextMoveId;

    // Open declarations plus the branch a completed one left HEAD on, held until Confirm.
    private sealed class MoveLog
    {
        public readonly List<(int Id, string Branch)> Open = new();
        public string? Landed;
    }

    public RepoHeadStore(IGitService git, IMessageBus bus, ILocalizationService loc, IUiDispatcher dispatcher)
    {
        _git = git;
        _bus = bus;
        _loc = loc;
        _dispatcher = dispatcher;
    }

    public RepoHead For(Guid repoId) => Get(repoId).Value;

    public Action<bool> BeginMove(Repo repo, string branchName)
    {
        var id = repo.Id;
        var log = Log(id);
        var move = ++_nextMoveId;
        log.Open.Add((move, branchName));
        Publish(id, log);
        return succeeded => Settle(id, move, succeeded);
    }

    private void Settle(Guid repoId, int move, bool succeeded)
    {
        if (_disposed) return;
        if (!_moves.TryGetValue(repoId, out var log)) return;
        var i = log.Open.FindIndex(m => m.Id == move);
        if (i < 0) return; // already settled — a settle is idempotent, not a second event
        var branch = log.Open[i].Branch;
        log.Open.RemoveAt(i);
        // A success leaves HEAD on this branch, and that name has to stand until a fresh read
        // confirms it — the command landing is not the moment the rest of the app stops being stale.
        // A failure moved nothing, so it contributes no destination at all.
        if (succeeded) log.Landed = branch;
        Publish(repoId, log);
    }

    // The destination is the newest declaration still open — with two moves queued on the repo lock,
    // the last one declared is the one that runs last and decides where HEAD ends up. With none open
    // it is whatever the last completed move landed on, until Confirm clears it.
    private void Publish(Guid repoId, MoveLog log)
        => Get(repoId).Value = log.Open.Count > 0
            ? new RepoHead(log.Open[^1].Branch, IsMoving: true)
            : new RepoHead(log.Landed, IsMoving: false);

    public void Checkout(Repo repo, string branchName)
    {
        if (Get(repo.Id).Value.IsMoving) return;
        RunMove(repo, branchName, () => _git.CheckoutLocalBranch(repo, branchName));
    }

    public void RunMove(Repo repo, string branchName, Func<GitOutcome> work, string? failureTitle = null)
    {
        var settle = BeginMove(repo, branchName);
        var dispatcher = _dispatcher;
        Task.Run(() =>
        {
            GitOutcome outcome;
            try { outcome = work(); }
            catch (Exception ex) { outcome = new GitOutcome.Failed(ex.Message); }
            dispatcher.Post(() => Complete(repo, outcome, settle, failureTitle));
        });
    }

    // Keyed on the captured repo, so the result lands on that repo's slot no matter which repo is
    // active when it finishes — and owned here rather than by the caller so that settling never
    // depends on a view model still being alive.
    private void Complete(Repo repo, GitOutcome outcome, Action<bool> settle, string? failureTitle)
    {
        if (_disposed) return;
        var failed = outcome as GitOutcome.Failed;
        settle(failed == null);

        _bus.Broadcast(new RefsChangedMessage(repo.Id));
        _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
        if (failed != null)
            _bus.Broadcast(new ShowOperationErrorMessage(
                failureTitle ?? _loc.Strings.Value.BranchesErrorCheckoutFailed, failed.Message));
    }

    private MoveLog Log(Guid id)
    {
        if (!_moves.TryGetValue(id, out var log))
        {
            log = new MoveLog();
            _moves[id] = log;
        }
        return log;
    }

    // Called whenever a fresh status read is accepted into a repo's slot. A read is only accepted if
    // it's the newest one, and every HEAD-moving path broadcasts RefsChangedMessage the instant its
    // command finishes — which kicks a newer read. So any accepted read arriving once IsMoving has
    // cleared observed the post-move HEAD, whatever it found. Settling on that rather than on a name
    // match is what stops a pending name outliving a move that landed somewhere unexpected.
    public void Confirm(Guid repoId)
    {
        if (_disposed) return;
        if (!_moves.TryGetValue(repoId, out var log)) return;
        // A read that arrived while a command is still open observed a HEAD that is about to move
        // again, so it confirms nothing.
        if (log.Open.Count > 0 || log.Landed == null) return;
        log.Landed = null;
        Publish(repoId, log);
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
        _moves.Clear();
    }
}
