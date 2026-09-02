using System.Collections.Concurrent;
using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// A checkout takes time, and for all of it the branch listing and the status probe still name the
// branch the user just left. Anything that seeds a git argument from that name — create branch,
// publish, review, merge — aims at the wrong branch. IRepoHeadStore holds the branch HEAD is moving
// to so those callers get one answer, and RepoStatus carries it to them.
//
// Drives the real head store and the real status store over throwaway repos.
public sealed class RepoHeadStoreTests : IDisposable
{
    private readonly string _root;
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly GitReadGate _gate = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly GitService _gitService = new(new RepoActivityTracker());
    private readonly RepoHeadStore _head;
    private readonly RepoStatusStore _status;

    public RepoHeadStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-head-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _head = new RepoHeadStore(_gitService, _bus, _loc, _dispatcher);
        _status = new RepoStatusStore(
            new IdleOperations(), _registry, _gitService, _bus, _gate, _dispatcher, _head, _head);
    }

    // The whole point of the store: from the instant the checkout starts, the name every caller reads
    // is the destination — not the branch the probe still reports.
    [Fact]
    public void A_started_checkout_names_the_destination_before_any_read_lands()
    {
        var repo = StartWithRepo("solo", "main");
        Git(repo.Path, "branch", "feature");
        Assert.Equal("main", _status.Active.Value.CurrentBranchName);

        _head.Checkout(repo, "feature");

        // Synchronous, like every other write to the slot — no drain window is needed, which is
        // exactly the window the old optimistic value left open for the toolbar to read "main".
        Assert.Equal("feature", _status.Active.Value.PendingBranchName);
        Assert.Equal("feature", _status.Active.Value.EffectiveBranchName);
        Assert.True(_status.Active.Value.IsHeadInMotion);
    }

    // The pending name has to outlive the git command: when it returns, every downstream reader is
    // still holding the pre-checkout name. It clears on the fresh read, which is when they stop being
    // stale — and then the two names agree rather than one replacing the other.
    [Fact]
    public void The_pending_name_clears_only_once_a_fresh_read_confirms_head()
    {
        var repo = StartWithRepo("solo", "main");
        Git(repo.Path, "branch", "feature");

        _head.Checkout(repo, "feature");
        DrainUntil(() => !_status.Active.Value.IsHeadInMotion, "the checkout to settle");

        Assert.Equal("feature", _status.Active.Value.CurrentBranchName);
        Assert.Null(_status.Active.Value.PendingBranchName);
        Assert.Equal("feature", _status.Active.Value.EffectiveBranchName);
    }

    // A failure never moved HEAD. Leaving the pending name standing would keep every caller aimed at
    // a branch we're not on — the same wrong-branch bug, just reached by a different route.
    [Fact]
    public void A_failed_checkout_drops_the_pending_name_rather_than_leaving_it_standing()
    {
        var repo = StartWithRepo("solo", "main");

        _head.Checkout(repo, "no-such-branch");
        DrainUntil(() => !_head.For(repo.Id).IsMoving, "the failed checkout to report");

        Assert.Null(_status.Active.Value.PendingBranchName);
        Assert.Equal("main", _status.Active.Value.EffectiveBranchName);
    }

    // Two repos switching at once must not read each other's destination: the slot is per repo, and
    // the result is keyed on the repo it was started for, not on whichever is active when it lands.
    [Fact]
    public void A_checkout_on_one_repo_leaves_another_repos_head_alone()
    {
        var alpha = StartWithRepo("alpha", "main");
        var beta = OpenRepo("beta", "main");
        Git(alpha.Path, "branch", "feature");

        _head.Checkout(alpha, "feature");

        Assert.Equal("feature", _head.For(alpha.Id).PendingBranch);
        Assert.Null(_head.For(beta.Id).PendingBranch);
    }

    // ---- overlapping declarations ----
    //
    // A settle must end only its own declaration. With a single slot, a second declaration overwrote
    // the first and then whichever settled first cleared a move that was still running — putting
    // every reader back on the stale probed name mid-switch, which is the failure this whole store
    // exists to prevent.

    [Fact]
    public void A_second_declaration_settling_first_leaves_the_still_running_move_declared()
    {
        var repo = Unbacked();

        var first = _head.BeginMove(repo, "feature");
        var second = _head.BeginMove(repo, "other");

        // The overlapping one reports first, and reports that it never moved anything.
        second(false);

        Assert.Equal("feature", _head.For(repo.Id).PendingBranch);
        Assert.True(_head.For(repo.Id).IsMoving);

        first(true);
        Assert.Equal("feature", _head.For(repo.Id).PendingBranch);
        Assert.False(_head.For(repo.Id).IsMoving);
    }

    // Two moves genuinely queued on the repo lock run in declaration order, so the newest one names
    // where HEAD ends up — and it holds that name until the older one is out of the way too.
    [Fact]
    public void The_newest_open_declaration_names_the_destination()
    {
        var repo = Unbacked();

        var first = _head.BeginMove(repo, "feature");
        _head.BeginMove(repo, "other");
        Assert.Equal("other", _head.For(repo.Id).PendingBranch);

        first(true);
        Assert.Equal("other", _head.For(repo.Id).PendingBranch);
        Assert.True(_head.For(repo.Id).IsMoving);
    }

    // A read landing while another command is still open observed a HEAD that is about to move
    // again, so it settles nothing.
    [Fact]
    public void A_read_arriving_mid_move_does_not_confirm_the_pending_name()
    {
        var repo = Unbacked();

        var first = _head.BeginMove(repo, "feature");
        var second = _head.BeginMove(repo, "other");
        first(true);

        ((IRepoHeadConfirm)_head).Confirm(repo.Id);
        Assert.Equal("other", _head.For(repo.Id).PendingBranch);

        second(true);
        ((IRepoHeadConfirm)_head).Confirm(repo.Id);
        Assert.Null(_head.For(repo.Id).PendingBranch);
    }

    // Settling is idempotent: a caller that settles twice must not consume a later declaration's slot.
    [Fact]
    public void Settling_twice_ends_only_the_one_declaration()
    {
        var repo = Unbacked();

        var first = _head.BeginMove(repo, "feature");
        first(false);
        var second = _head.BeginMove(repo, "other");
        first(false);

        Assert.Equal("other", _head.For(repo.Id).PendingBranch);
        Assert.True(_head.For(repo.Id).IsMoving);
        second(false);
        Assert.Null(_head.For(repo.Id).PendingBranch);
    }

    // RunMove owns the command, so its settle and its refresh don't depend on the view model that
    // started it still being alive — a repo switch mid-move used to strand the declaration, and a
    // stranded declaration wedges every later checkout shut.
    [Fact]
    public void RunMove_settles_without_help_from_the_caller()
    {
        var repo = StartWithRepo("solo", "main");
        Git(repo.Path, "branch", "feature");

        _head.RunMove(repo, "feature", () => _gitService.CheckoutLocalBranch(repo, "feature"));
        Assert.True(_head.For(repo.Id).IsMoving);

        DrainUntil(() => !_status.Active.Value.IsHeadInMotion, "the move to settle on its own");
        Assert.Equal("feature", _status.Active.Value.CurrentBranchName);

        // And the store is left able to move HEAD again, which a stranded declaration would prevent.
        _head.Checkout(repo, "main");
        Assert.Equal("main", _head.For(repo.Id).PendingBranch);
    }

    // ---- helpers ----

    // A repo identity with nothing on disk behind it, for the declaration bookkeeping — which is
    // pure and never reaches git. Keeps those cases off the process-spawning path.
    private static Repo Unbacked() => new(Guid.NewGuid(), "not-on-disk", "solo");

    private Repo StartWithRepo(string name, string branch)
    {
        var repo = OpenRepo(name, branch);
        _status.Start();
        _registry.SetActive(repo.Id);
        DrainUntil(() => _status.Active.Value.CurrentBranchName == branch, "the initial probe to land");
        return repo;
    }

    private Repo OpenRepo(string name, string branch)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", branch);
        Git(path, "config", "user.name", "Test");
        Git(path, "config", "user.email", "test@example.com");
        Git(path, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(path, "a.txt"), "0");
        Git(path, "add", "a.txt");
        Git(path, "commit", "-qm", "base");
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(path));
        return _registry.Repos.Single(r => r.DisplayName == name);
    }

    // The checkout and the probe both run off-thread and post back, so pump the dispatcher until the
    // stores reflect them.
    private void DrainUntil(Func<bool> done, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            _dispatcher.Drain();
            if (done()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");
    }

    public void Dispose()
    {
        _status.Dispose();
        _head.Dispose();
        _registry.Dispose();
        _loc.Dispose();
        DirectoryTree.Delete(_root);
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public void Post(Action action) => _queue.Enqueue(action);

        public void Drain()
        {
            while (_queue.TryDequeue(out var action)) action();
        }
    }

    private sealed class IdleOperations : IRepoOperationsStore
    {
        private readonly State<RepoOperations> _active = new(RepoOperations.Idle);

        public IReadable<RepoOperations> Active => _active;
        public bool HasUnseenError(Guid repoId) => false;
        public bool IsBusy(Guid repoId) => false;
        public void Push(Repo repo, bool force = false) { }
        public void Pull(Repo repo, PullStrategy? strategy = null) { }
        public void Fetch(Repo repo) { }
        public Task<RemoteOpResult> PullAsync(Repo repo, PullStrategy? strategy = null) => Task.FromResult(RemoteOpResult.Ok);
        public Task<RemoteOpResult> FetchAsync(Repo repo) => Task.FromResult(RemoteOpResult.Ok);
    }
}
