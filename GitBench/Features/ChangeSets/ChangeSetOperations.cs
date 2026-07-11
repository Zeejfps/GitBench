using System.Text;
using GitBench.Features.LocalChanges;
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

    /// <summary>
    /// Starts a change set (Phase 4): creates a branch named <paramref name="branchName"/> in every
    /// selected member, each from its own start point, checking it out (<c>checkout: true</c>) so all
    /// members switch to the new branch at once. Same per-repo, no-rollback semantics as the other ops
    /// (Locked decision #5) — a name collision in one repo fails that repo alone and still creates the
    /// branch in the others. Reporting broadcasts <see cref="RefsChangedMessage"/> per member, so
    /// <c>SyncedBranchIndex</c> re-detects the fresh set immediately (4.2) rather than on a timer.
    /// </summary>
    public void CreateInAll(IReadOnlyList<(Guid RepoId, string StartPoint)> members, string branchName)
    {
        var startById = new Dictionary<Guid, string>(members.Count);
        var ids = new List<Guid>(members.Count);
        foreach (var (id, startPoint) in members)
        {
            startById[id] = startPoint;
            ids.Add(id);
        }
        Run(ids,
            op: r => _git.CreateBranch(r, branchName, ResolveStartPoint(startById, r.Id), checkout: true),
            success: (s, count) => s.ChangesetsToastCreate(branchName, count),
            touchesWorkingTree: true);
    }

    /// <summary>
    /// The start point for a member, trimmed, falling back to <c>HEAD</c> when the field was left blank
    /// — matching <c>CreateBranchDialog</c>'s single-repo behavior. Pure, so <see cref="CreateInAll"/>'s
    /// per-repo start-point mapping is unit-testable directly (the coordinator itself being
    /// fire-and-forget).
    /// </summary>
    internal static string ResolveStartPoint(IReadOnlyDictionary<Guid, string> startById, Guid repoId) =>
        startById.TryGetValue(repoId, out var sp) && sp.Trim().Length > 0 ? sp.Trim() : "HEAD";

    /// <summary>
    /// Batch commit across a change set (Phase 5.4): commits <paramref name="message"/> — stamped with
    /// the <c>Change-Set: &lt;name&gt;</c> trailer (Locked decision #6) — in every member that has
    /// staged changes. Members with nothing staged simply don't commit (5.5: no auto-staging), and a
    /// member whose commit fails never blocks the rest (Locked decision #5, no rollback). Fire-and-forget
    /// with the shared per-repo summary reporting; the durable trailer outlives branch deletion and is the
    /// hook any future PR/submit integration keys off.
    /// </summary>
    public void CommitInAll(IReadOnlyList<Guid> repoIds, string message, string changeSetName)
    {
        var members = Resolve(repoIds);
        if (members.Count == 0) return;
        var full = StampTrailer(message, changeSetName);

        Task.Run(() =>
        {
            var result = CommitOverMembers(members, HasStagedChanges, r => _git.Commit(r, full, amend: false));
            _dispatcher.Post(() => Report(
                result,
                success: (s, count) => s.ChangesetsToastCommit(changeSetName, count),
                touchesWorkingTree: true,
                commitCreated: true));
        });
    }

    /// <summary>
    /// Appends the <c>Change-Set: &lt;name&gt;</c> trailer to a commit message (Locked decision #6): the
    /// existing message, its trailing whitespace trimmed, then a blank line and the trailer. Pure, so the
    /// trailer stamping is unit-testable directly (the coordinator itself being fire-and-forget).
    /// </summary>
    internal static string StampTrailer(string message, string changeSetName) =>
        $"{message.TrimEnd()}\n\nChange-Set: {changeSetName}";

    /// <summary>
    /// The per-member commit core (Phase 5.4): commits only the members that have staged changes, folding
    /// a thrown call into that member's <see cref="GitOutcome.Failed"/> so one failure never aborts the
    /// loop and never rolls anything back (Locked decision #5). A member with nothing staged is skipped —
    /// it contributes no outcome at all (it didn't commit), so it isn't counted as a success or a failure.
    /// Pure and synchronous, so the skip-unstaged and no-rollback guarantees are unit-testable directly.
    /// </summary>
    internal static ChangeSetOpResult CommitOverMembers(
        IReadOnlyList<Repo> members, Func<Repo, bool> hasStaged, Func<Repo, GitOutcome> commit)
    {
        var results = new List<(Guid, GitOutcome)>(members.Count);
        foreach (var repo in members)
        {
            if (!hasStaged(repo)) continue; // nothing staged → don't commit (5.5: no auto-staging)
            GitOutcome outcome;
            try { outcome = commit(repo); }
            catch (Exception ex) { outcome = new GitOutcome.Failed(ex.Message); }
            results.Add((repo.Id, outcome));
        }
        return new ChangeSetOpResult(results);
    }

    // Whether the repo has anything in its index to commit. A failed probe reads as "nothing staged" so
    // the member is skipped rather than committed blindly.
    private bool HasStagedChanges(Repo repo)
    {
        try
        {
            return _git.GetLocalChanges(repo) is Fetched<LocalChangesSnapshot>.Ok ok && ok.Value.Staged.Count > 0;
        }
        catch
        {
            return false;
        }
    }

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

    private void Report(
        ChangeSetOpResult result,
        Func<Strings, int, string> success,
        bool touchesWorkingTree,
        bool commitCreated = false)
    {
        // Every member reloads whether it succeeded or failed: a reload just re-reads git, so it
        // reflects reality either way (and a failed op may still have moved something).
        foreach (var (repoId, _) in result.Results)
        {
            _bus.Broadcast(new RefsChangedMessage(repoId));
            if (touchesWorkingTree)
                _bus.Broadcast(new WorkingTreeChangedMessage(repoId));
            // A batch commit folds each member's staged files into a new commit — the same signal a
            // single-repo commit broadcasts, so History and the review surfaces pick up the increment.
            if (commitCreated)
                _bus.Broadcast(new CommitCreatedMessage(repoId));
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
