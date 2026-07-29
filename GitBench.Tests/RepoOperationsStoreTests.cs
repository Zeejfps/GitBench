using System.Diagnostics;
using GitBench.Features.Notifications;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// The push/pull/fetch lifecycle as the rest of the app observes it: the spinner flags, the toast,
// the optimistic ahead/behind snap, and where a failure lands depending on whether the repo is the
// one on screen. Pinned before the store grows an awaitable path, so that refactor can be shown to
// preserve behaviour rather than asserted to. git is scripted here on purpose — the question is what
// the store does with an outcome, not what git does with a remote.
public sealed class RepoOperationsStoreTests : IDisposable
{
    private readonly TempDir _root = new("gitbench-ops-store-");
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly ScriptedRemoteGitService _git;
    private readonly RepoOperationsStore _store;
    private readonly Repo _onScreen;
    private readonly Repo _background;

    private readonly List<ShowToastMessage> _toasts = new();
    private readonly List<RefsChangedMessage> _refs = new();
    private readonly List<RemoteSyncOptimisticMessage> _optimistic = new();
    private readonly List<ShowOperationErrorMessage> _errorDialogs = new();
    private readonly List<PullDivergedMessage> _diverged = new();

    public RepoOperationsStoreTests()
    {
        var statePath = Path.Combine(_root.Path, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _onScreen = Open("on-screen");
        _background = Open("background");
        _registry.SetActive(_onScreen.Id);

        _git = new ScriptedRemoteGitService(new GitService(new RepoActivityTracker()));
        _store = new RepoOperationsStore(_registry, _git, _bus, _loc, _dispatcher);
        _store.Start();

        _bus.Subscribe<ShowToastMessage>(_toasts.Add);
        _bus.Subscribe<RefsChangedMessage>(_refs.Add);
        _bus.Subscribe<RemoteSyncOptimisticMessage>(_optimistic.Add);
        _bus.Subscribe<ShowOperationErrorMessage>(_errorDialogs.Add);
        _bus.Subscribe<PullDivergedMessage>(_diverged.Add);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        _root.Dispose();
    }

    // The toolbar button disables off this flag the moment it is pressed; a fetch that only raised
    // it a dispatcher hop later would let a double-click start two.
    [Fact]
    public void Fetch_RaisesTheSpinnerFlagBeforeItReturns_AndLowersItWhenTheFetchLands()
    {
        _git.OnFetch = _ => GitOutcome.Ok;

        _store.Fetch(_onScreen);

        Assert.True(_store.Active.Value.IsFetching);
        Assert.True(_store.IsBusy(_onScreen.Id));

        Settle(() => !_store.IsBusy(_onScreen.Id), "the fetch to complete");

        Assert.False(_store.Active.Value.IsFetching);
    }

    [Fact]
    public void Fetch_OnSuccess_BroadcastsRefsChangedAndTheFetchedToast()
    {
        _git.OnFetch = _ => GitOutcome.Ok;

        _store.Fetch(_onScreen);
        Settle(() => _toasts.Count > 0, "the success toast");

        Assert.Equal(_onScreen.Id, Assert.Single(_refs).RepoId);
        Assert.Equal(_loc.Strings.Value.ToastFetched, Assert.Single(_toasts).Intent.Message);
        Assert.Empty(_errorDialogs);
    }

    // A fetch moves the remote-tracking refs, not the branch, so the store deliberately declines to
    // guess an ahead/behind — unlike push and pull, which know their own outcome.
    [Fact]
    public void Fetch_OnSuccess_DoesNotSnapAheadOrBehind()
    {
        _git.OnFetch = _ => GitOutcome.Ok;

        _store.Fetch(_onScreen);
        Settle(() => _toasts.Count > 0, "the success toast");

        Assert.Empty(_optimistic);
    }

    [Fact]
    public void Push_OnSuccess_SnapsAheadToZero()
    {
        _git.OnPush = (_, _) => GitOutcome.Ok;

        _store.Push(_onScreen);
        Settle(() => _toasts.Count > 0, "the success toast");

        Assert.Equal(new RemoteSyncOptimisticMessage(_onScreen.Id, 0, null), Assert.Single(_optimistic));
    }

    [Fact]
    public void Pull_OnSuccess_SnapsBehindToZero()
    {
        _git.OnPull = (_, _) => PullOutcome.Ok;

        _store.Pull(_onScreen);
        Settle(() => _toasts.Count > 0, "the success toast");

        Assert.Equal(new RemoteSyncOptimisticMessage(_onScreen.Id, null, 0), Assert.Single(_optimistic));
    }

    [Fact]
    public void Pull_ForwardsTheStrategyItWasGiven()
    {
        _git.OnPull = (_, _) => PullOutcome.Ok;

        _store.Pull(_onScreen, PullStrategy.Rebase);
        Settle(() => _toasts.Count > 0, "the success toast");

        Assert.Equal(PullStrategy.Rebase, Assert.Single(_git.PullStrategies));
    }

    [Fact]
    public void Pull_WithoutAStrategy_AsksGitForItsConfiguredDefault()
    {
        _git.OnPull = (_, _) => PullOutcome.Ok;

        _store.Pull(_onScreen);
        Settle(() => _toasts.Count > 0, "the success toast");

        Assert.Null(Assert.Single(_git.PullStrategies));
    }

    // The in-flight flag is only lowered by the completion the dispatcher has not run yet, so the
    // second call lands squarely in the already-running case.
    [Fact]
    public void Fetch_WhileAFetchIsAlreadyRunningOnThatRepo_StartsNothingFurther()
    {
        _git.OnFetch = _ => GitOutcome.Ok;

        _store.Fetch(_onScreen);
        _store.Fetch(_onScreen);
        Settle(() => _toasts.Count > 0, "the success toast");

        Assert.Equal(1, _git.FetchCalls);
        Assert.Single(_toasts);
    }

    // Same-type only: the flag guards a second fetch, not a pull that wants to run alongside it.
    [Fact]
    public void Pull_WhileAFetchIsRunning_StillRuns()
    {
        _git.OnFetch = _ => GitOutcome.Ok;
        _git.OnPull = (_, _) => PullOutcome.Ok;

        _store.Fetch(_onScreen);
        _store.Pull(_onScreen);
        Settle(() => _toasts.Count >= 2, "both success toasts");

        Assert.Equal(1, _git.FetchCalls);
        Assert.Equal(1, _git.PullCalls);
    }

    [Fact]
    public void Fetch_FailingOnTheRepoOnScreen_ShowsTheErrorDialogAndLeavesNoBadge()
    {
        _git.OnFetch = _ => GitOutcome.Fail("could not read from remote repository");

        _store.Fetch(_onScreen);
        Settle(() => _errorDialogs.Count > 0, "the error dialog");

        var shown = Assert.Single(_errorDialogs);
        Assert.Equal(_loc.Strings.Value.ReposErrorFetchFailed, shown.Title);
        Assert.Equal("could not read from remote repository", shown.Message);
        Assert.False(_store.HasUnseenError(_onScreen.Id));
        Assert.Empty(_toasts);
    }

    [Fact]
    public void Fetch_FailingOnARepoTheUserIsNotLookingAt_ParksAPendingErrorBadge()
    {
        _git.OnFetch = _ => GitOutcome.Fail("host unreachable");

        _store.Fetch(_background);
        Settle(() => _store.HasUnseenError(_background.Id), "the pending error badge");

        Assert.Empty(_errorDialogs);
    }

    [Fact]
    public void SwitchingToARepoWithAnUnseenFailure_ShowsItAsADialogAndClearsTheBadge()
    {
        _git.OnFetch = _ => GitOutcome.Fail("host unreachable");
        _store.Fetch(_background);
        Settle(() => _store.HasUnseenError(_background.Id), "the pending error badge");

        _registry.SetActive(_background.Id);

        Assert.Equal("host unreachable", Assert.Single(_errorDialogs).Message);
        Assert.False(_store.HasUnseenError(_background.Id));
    }

    [Fact]
    public void StartingAnOperation_ClearsAnyPendingErrorFromTheLastOne()
    {
        _git.OnFetch = _ => GitOutcome.Fail("host unreachable");
        _store.Fetch(_background);
        Settle(() => _store.HasUnseenError(_background.Id), "the pending error badge");

        _git.OnFetch = _ => GitOutcome.Ok;
        _store.Fetch(_background);

        Assert.False(_store.HasUnseenError(_background.Id));
    }

    // A diverged pull on the repo in front of the user is recoverable in-app, so it goes to the
    // reconcile dialog rather than the generic failure dialog.
    [Fact]
    public void Pull_DivergingOnTheRepoOnScreen_HandsTheDivergenceToTheReconcileDialog()
    {
        _git.OnPull = (_, _) => new PullOutcome.Diverged();

        _store.Pull(_onScreen);
        Settle(() => _diverged.Count > 0, "the diverged message");

        Assert.Equal(_onScreen, Assert.Single(_diverged).Repo);
        Assert.Empty(_errorDialogs);
        Assert.False(_store.HasUnseenError(_onScreen.Id));
        Assert.Empty(_toasts);
    }

    // Nothing to reconcile against on a background repo — it falls through to the badge and the
    // user re-pulls when they switch to it.
    [Fact]
    public void Pull_DivergingOnARepoTheUserIsNotLookingAt_ParksThePendingErrorBadgeInstead()
    {
        _git.OnPull = (_, _) => new PullOutcome.Diverged();

        _store.Pull(_background);
        Settle(() => _store.HasUnseenError(_background.Id), "the pending error badge");

        Assert.Empty(_diverged);
    }

    // The completion is keyed by the repo the op started on, so switching away mid-fetch must not
    // deliver one repo's failure to another's slice.
    [Fact]
    public void Fetch_CompletingAfterTheUserSwitchedAway_LandsOnTheRepoItStartedOn()
    {
        _git.OnFetch = _ => GitOutcome.Fail("host unreachable");

        _store.Fetch(_onScreen);
        _registry.SetActive(_background.Id);
        Settle(() => _store.HasUnseenError(_onScreen.Id), "the pending error badge on the fetched repo");

        Assert.False(_store.HasUnseenError(_background.Id));
        Assert.Empty(_errorDialogs);
    }

    // git throwing rather than returning a failure is the path that loses a completion if the
    // lifecycle is unwound anywhere but in the completion itself.
    [Fact]
    public void Fetch_WhenGitThrows_ReportsTheExceptionAndClearsTheSpinner()
    {
        _git.OnFetch = _ => throw new InvalidOperationException("git exploded");

        _store.Fetch(_onScreen);
        Settle(() => _errorDialogs.Count > 0, "the error dialog");

        Assert.Equal("git exploded", Assert.Single(_errorDialogs).Message);
        Assert.False(_store.IsBusy(_onScreen.Id));
    }

    [Fact]
    public void Active_SwapsToTheSliceOfWhicheverRepoIsOnScreen()
    {
        _git.OnFetch = _ => GitOutcome.Ok;

        _store.Fetch(_background);

        Assert.False(_store.Active.Value.IsFetching);

        _registry.SetActive(_background.Id);

        Assert.True(_store.Active.Value.IsFetching);
    }

    // ---- the awaitable path, for a caller that has to report what happened ----

    [Fact]
    public async Task FetchAsync_OnSuccess_CompletesAsSucceeded()
    {
        _git.OnFetch = _ => GitOutcome.Ok;

        var task = _store.FetchAsync(_onScreen);
        Settle(() => task.IsCompleted, "the fetch to complete");

        Assert.IsType<RemoteOpResult.Succeeded>(await task);
    }

    // The same guarantee the void caller already has, at the entry point it now delegates to: the
    // state read and the flag write both happen before the task is handed back, so an off-UI-thread
    // caller has to marshal to here rather than the other way round.
    [Fact]
    public void FetchAsync_RaisesTheSpinnerFlagBeforeItHandsBackTheTask()
    {
        _git.OnFetch = _ => GitOutcome.Ok;

        _ = _store.FetchAsync(_onScreen);

        Assert.True(_store.IsBusy(_onScreen.Id));
    }

    // The message the caller reports has to be the one the error dialog would have shown, or the two
    // accounts of the same failure disagree.
    [Fact]
    public async Task FetchAsync_WhenGitFails_CompletesAsFailedCarryingWhatTheDialogWouldSay()
    {
        _git.OnFetch = _ => GitOutcome.Fail("could not read from remote repository");

        var task = _store.FetchAsync(_onScreen);
        Settle(() => task.IsCompleted, "the fetch to complete");

        var failed = Assert.IsType<RemoteOpResult.Failed>(await task);
        Assert.Equal("could not read from remote repository", failed.Message);
        Assert.Equal(failed.Message, Assert.Single(_errorDialogs).Message);
    }

    // An ordinary git failure is a result, not an exception — a caller that has to `try` around
    // every fetch will eventually forget to.
    [Fact]
    public async Task FetchAsync_WhenGitThrows_CompletesAsFailedRatherThanFaulting()
    {
        _git.OnFetch = _ => throw new InvalidOperationException("git exploded");

        var task = _store.FetchAsync(_onScreen);
        Settle(() => task.IsCompleted, "the fetch to complete");

        Assert.False(task.IsFaulted);
        Assert.Equal("git exploded", Assert.IsType<RemoteOpResult.Failed>(await task).Message);
    }

    // Nothing was started, and the result has to say that rather than resolving to the outcome of
    // the fetch that was already running — which would report someone else's success as this
    // caller's.
    [Fact]
    public async Task FetchAsync_WhileAFetchIsAlreadyRunning_CompletesAsAlreadyRunningWithoutCallingGit()
    {
        _git.OnFetch = _ => GitOutcome.Ok;

        var first = _store.FetchAsync(_onScreen);
        var second = _store.FetchAsync(_onScreen);

        Assert.True(second.IsCompleted);
        Assert.IsType<RemoteOpResult.AlreadyRunning>(await second);

        Settle(() => first.IsCompleted, "the first fetch to complete");
        Assert.Equal(1, _git.FetchCalls);
    }

    [Fact]
    public async Task PullAsync_WhenTheBranchDiverges_CompletesAsDivergedRatherThanFailed()
    {
        _git.OnPull = (_, _) => new PullOutcome.Diverged();

        var task = _store.PullAsync(_onScreen);
        Settle(() => task.IsCompleted, "the pull to complete");

        Assert.IsType<RemoteOpResult.Diverged>(await task);
    }

    // The store only broadcasts PullDivergedMessage for the repo on screen, but the divergence is a
    // fact about the pull either way — a caller told "failed" here would report the wrong reason.
    [Fact]
    public async Task PullAsync_DivergingOnARepoTheUserIsNotLookingAt_StillCompletesAsDiverged()
    {
        _git.OnPull = (_, _) => new PullOutcome.Diverged();

        var task = _store.PullAsync(_background);
        Settle(() => task.IsCompleted, "the pull to complete");

        Assert.IsType<RemoteOpResult.Diverged>(await task);
        Assert.Empty(_diverged);
        Assert.True(_store.HasUnseenError(_background.Id));
    }

    [Fact]
    public async Task PullAsync_ForwardsTheStrategyItWasGiven()
    {
        _git.OnPull = (_, _) => PullOutcome.Ok;

        var task = _store.PullAsync(_onScreen, PullStrategy.Rebase);
        Settle(() => task.IsCompleted, "the pull to complete");

        await task;
        Assert.Equal(PullStrategy.Rebase, Assert.Single(_git.PullStrategies));
    }

    // Shutdown drops the completion that would otherwise have resolved the task; an awaiter left
    // hanging on it is a hang in whatever was waiting for the fetch to report.
    [Fact]
    public void FetchAsync_WhenTheStoreIsDisposedMidFlight_CompletesTheOutstandingTaskRatherThanStranding()
    {
        _git.OnFetch = _ => GitOutcome.Ok;
        var task = _store.FetchAsync(_onScreen);

        _store.Dispose();

        Assert.True(task.IsCompleted);
        Assert.False(task.IsFaulted);
        Assert.IsType<RemoteOpResult.Failed>(task.GetAwaiter().GetResult());
    }

    private void Settle(Func<bool> done, string what) => Pump.WaitFor(_dispatcher, done, what);

    private Repo Open(string name)
    {
        var path = Path.Combine(_root.Path, name);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        Git(path, "config", "user.email", "test@test");
        Git(path, "config", "user.name", "test");
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(path));
        return _registry.Repos.Single(r => r.Path == path);
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }
}
