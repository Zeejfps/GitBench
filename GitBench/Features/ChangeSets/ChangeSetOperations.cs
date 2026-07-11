using System.Text;
using GitBench.Features.Notifications;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.ChangeSets;

// A batch operation's honest outcome: one GitOutcome per member, never rolled up into a single
// bool (Locked decision #5). Partial success is a first-class result — a failed member is recorded
// beside the ones that succeeded, and never rolls anything back.
public sealed record ChangeSetOpResult(
    IReadOnlyList<(Guid RepoId, GitOutcome Outcome)> Results)
{
    public int SuccessCount
    {
        get
        {
            var n = 0;
            foreach (var (_, outcome) in Results)
                if (outcome is not GitOutcome.Failed) n++;
            return n;
        }
    }

    public bool AllSucceeded => SuccessCount == Results.Count;
}

/// <summary>
/// Coordinator for batch actions over a synced branch's members (Phase 2). Each public method loops
/// one <see cref="IGitService"/> call per member off the UI thread (the <c>RunBackground</c>
/// convention: work on a worker thread, results posted back through <see cref="IUiDispatcher"/>),
/// collects a per-repo <see cref="ChangeSetOpResult"/>, refreshes each member (broadcasting
/// <see cref="RefsChangedMessage"/>), and reports one summary: a success toast when every member
/// succeeds, or a warning toast whose "Details" action opens the per-repo failure breakdown.
///
/// Locked decision #5: operations are plain loops with no rollback. A failed member never blocks the
/// others — the loop runs every member and records each outcome independently, so partial success is
/// a visible, honest result rather than an error to unwind.
/// </summary>
internal sealed class ChangeSetOperations
{
    private readonly IGitService _git;
    private readonly IRepoRegistry _registry;
    private readonly IMessageBus _bus;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILocalizationService _loc;

    public ChangeSetOperations(
        IGitService git,
        IRepoRegistry registry,
        IMessageBus bus,
        IUiDispatcher dispatcher,
        ILocalizationService loc)
    {
        _git = git;
        _registry = registry;
        _bus = bus;
        _dispatcher = dispatcher;
        _loc = loc;
    }

    /// <summary>
    /// Runs <paramref name="op"/> once per member in order, folding a thrown exception into that
    /// member's <see cref="GitOutcome.Failed"/> so one member's failure never aborts the loop. Pure
    /// and synchronous — no threading, no I/O of its own — so the coordinator's essential behavior
    /// (per-repo outcomes, no rollback, a failed member doesn't block the rest) is unit-testable
    /// directly over a fake <see cref="IGitService"/>.
    /// </summary>
    public static ChangeSetOpResult RunOverMembers(IReadOnlyList<Repo> members, Func<Repo, GitOutcome> op)
    {
        var results = new List<(Guid, GitOutcome)>(members.Count);
        foreach (var repo in members)
        {
            GitOutcome outcome;
            try { outcome = op(repo); }
            catch (Exception ex) { outcome = new GitOutcome.Failed(ex.Message); }
            results.Add((repo.Id, outcome));
        }
        return new ChangeSetOpResult(results);
    }

    public void CheckoutInAll(IReadOnlyList<Guid> repoIds, string branchName) =>
        Run(repoIds,
            op: r => _git.CheckoutLocalBranch(r, branchName),
            success: (s, count) => s.ChangesetsToastCheckout(branchName, count),
            touchesWorkingTree: true);

    public void PushInAll(IReadOnlyList<Guid> repoIds) =>
        Run(repoIds,
            op: r => _git.Push(r),
            success: (s, count) => s.ChangesetsToastPush(count),
            touchesWorkingTree: false);

    public void PullInAll(IReadOnlyList<Guid> repoIds) =>
        Run(repoIds,
            op: r => _git.Pull(r) switch
            {
                PullOutcome.Failed failed => new GitOutcome.Failed(failed.Message),
                PullOutcome.Diverged => new GitOutcome.Failed(_loc.Strings.Value.ChangesetsPullDiverged),
                _ => GitOutcome.Ok,
            },
            success: (s, count) => s.ChangesetsToastPull(count),
            touchesWorkingTree: true);

    public void FetchInAll(IReadOnlyList<Guid> repoIds) =>
        Run(repoIds,
            op: r => _git.Fetch(r),
            success: (s, count) => s.ChangesetsToastFetch(count),
            touchesWorkingTree: false);

    public void DeleteInAll(IReadOnlyList<Guid> repoIds, string branchName, bool force) =>
        Run(repoIds,
            op: r => _git.DeleteBranch(r, branchName, force),
            success: (s, count) => s.ChangesetsToastDelete(branchName, count),
            touchesWorkingTree: false);

    private void Run(
        IReadOnlyList<Guid> repoIds,
        Func<Repo, GitOutcome> op,
        Func<Strings, int, string> success,
        bool touchesWorkingTree)
    {
        var members = Resolve(repoIds);
        if (members.Count == 0) return;

        Task.Run(() =>
        {
            var result = RunOverMembers(members, op);
            _dispatcher.Post(() => Report(result, success, touchesWorkingTree));
        });
    }

    // Resolves member ids to their current Repo, dropping any that no longer exist (removed while a
    // menu was open). Order follows the id list, which is the set's group-membership order.
    private IReadOnlyList<Repo> Resolve(IReadOnlyList<Guid> repoIds)
    {
        var repos = new List<Repo>(repoIds.Count);
        foreach (var id in repoIds)
            if (FindRepo(id) is { } repo) repos.Add(repo);
        return repos;
    }

    private void Report(ChangeSetOpResult result, Func<Strings, int, string> success, bool touchesWorkingTree)
    {
        // Every member reloads whether it succeeded or failed: a reload just re-reads git, so it
        // reflects reality either way (and a failed op may still have moved something).
        foreach (var (repoId, _) in result.Results)
        {
            _bus.Broadcast(new RefsChangedMessage(repoId));
            if (touchesWorkingTree)
                _bus.Broadcast(new WorkingTreeChangedMessage(repoId));
        }

        var s = _loc.Strings.Value;
        if (result.AllSucceeded)
        {
            _bus.Broadcast(new ShowToastMessage(ToastIntent.Success(success(s, result.Results.Count))));
            return;
        }

        // Partial (or total) failure: a summary toast whose action opens the per-repo breakdown in
        // the scrollable operation-error dialog. Never rolls back the members that succeeded.
        var detail = BuildFailureDetail(result);
        _bus.Broadcast(new ShowToastMessage(ToastIntent.Warning(
            s.ChangesetsToastPartial(result.SuccessCount, result.Results.Count),
            new ToastAction(
                s.ChangesetsDetails,
                () => _bus.Broadcast(new ShowOperationErrorMessage(s.ChangesetsOpFailedTitle, detail))))));
    }

    private string BuildFailureDetail(ChangeSetOpResult result)
    {
        var sb = new StringBuilder();
        foreach (var (repoId, outcome) in result.Results)
        {
            if (outcome is not GitOutcome.Failed failed) continue;
            var name = DisplayNameOf(repoId) ?? repoId.ToString();
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(name).Append(": ").Append(failed.Message);
        }
        return sb.ToString();
    }

    private Repo? FindRepo(Guid id)
    {
        foreach (var r in _registry.Repos)
            if (r.Id == id) return r;
        return null;
    }

    private string? DisplayNameOf(Guid id) => FindRepo(id)?.DisplayName;
}
