using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Backs the bottom <see cref="StatusBarView"/>: the active repo name, current branch, and
/// ahead/behind counts, plus the theme toggle. The repo name comes straight from the registry;
/// the branch and ahead/behind are projected from <see cref="IRepoSnapshotStore.PushStatus"/>
/// (derived from the branch listing), so there's no separate git query here.
/// </summary>
internal sealed class StatusBarViewModel : ViewModelBase<StatusBarState>
{
    // How long a manual check's "up to date" / "failed" result lingers before clearing itself.
    private const int FeedbackLingerMs = 4000;

    private readonly IRepoRegistry _registry;
    private readonly State<ThemeMode> _themeMode;
    private readonly UpdateService _updateService;
    private readonly SpinnerAnimation _updateSpinner;
    private CancellationTokenSource? _feedbackCts;

    public IReadable<bool> HasActiveRepo { get; }
    public IReadable<string?> RepoName { get; }
    public IReadable<string?> Branch { get; }
    public IReadable<bool> HasBranch { get; }
    public IReadable<bool> ShowAhead { get; }
    public IReadable<bool> ShowBehind { get; }
    public IReadable<string> AheadText { get; }
    public IReadable<string> BehindText { get; }

    public Command ToggleTheme { get; }
    public IReadable<ThemeMode> Theme => _themeMode;

    public Command CheckForUpdates { get; }
    public IReadable<bool> IsCheckingUpdates => _updateService.IsChecking;
    public IReadable<float> UpdateIconRotation => _updateSpinner.Rotation;
    public IReadable<string?> UpdateCheckFeedback => _updateService.CheckFeedback;

    public StatusBarViewModel(
        IRepoRegistry registry,
        IUiDispatcher dispatcher,
        IRepoSnapshotStore store,
        State<ThemeMode> themeMode,
        UpdateService updateService)
        : base(dispatcher, StatusBarState.Initial)
    {
        _registry = registry;
        _themeMode = themeMode;
        _updateService = updateService;
        _updateSpinner = new SpinnerAnimation(dispatcher);

        HasActiveRepo = Slice(s => s.HasActiveRepo);
        RepoName = Slice(s => s.RepoName);
        Branch = Slice(s => s.Branch);
        HasBranch = Slice(s => !string.IsNullOrEmpty(s.Branch));
        ShowAhead = Slice(s => HasTracking(s) && s.Ahead > 0);
        ShowBehind = Slice(s => HasTracking(s) && s.Behind > 0);
        AheadText = Slice(s => s.Ahead.ToString());
        BehindText = Slice(s => s.Behind.ToString());

        ToggleTheme = new Command(DoToggleTheme);
        CheckForUpdates = new Command(DoCheckForUpdates);

        // Spin the check button's icon for the duration of a check; fires immediately with the
        // current (idle) value, leaving the spinner stopped until a check starts.
        Subscriptions.Add(_updateService.IsChecking.Subscribe(OnCheckingChanged));
        // A finished manual check's result clears itself after a short linger.
        Subscriptions.Add(_updateService.CheckFeedback.Subscribe(OnFeedbackChanged));

        // Repo name / has-repo come from the registry (instant, non-git); branch + ahead/behind
        // are refined from the store's push status. Both subscriptions fire immediately.
        Subscriptions.Add(_registry.Active.Subscribe(OnActiveChanged));
        Subscriptions.Add(store.PushStatus.Subscribe(OnPushStatus));
    }

    private static bool HasTracking(StatusBarState s) => s.HasUpstream && !s.IsDetached;

    private void DoToggleTheme() =>
        _themeMode.Value = _themeMode.Value == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;

    private void DoCheckForUpdates() =>
        _ = _updateService.CheckForUpdatesAsync(Dispatcher, userInitiated: true);

    private void OnCheckingChanged(bool checking)
    {
        if (checking) _updateSpinner.Start();
        else _updateSpinner.Stop();
    }

    // Auto-clears a manual check's inline result after a linger. Mirrors the Tooltip's
    // cancel-on-supersede pattern: a fresh message (or another check, which nulls feedback
    // first) cancels the pending clear so it never wipes a newer message.
    private void OnFeedbackChanged(string? message)
    {
        _feedbackCts?.Cancel();
        _feedbackCts?.Dispose();
        _feedbackCts = null;
        if (string.IsNullOrEmpty(message)) return;

        var cts = new CancellationTokenSource();
        _feedbackCts = cts;
        var token = cts.Token;
        var dispatcher = Dispatcher;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FeedbackLingerMs, token).ConfigureAwait(false);
                dispatcher.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _updateService.CheckFeedback.Value = null;
                });
            }
            catch (OperationCanceledException) { /* superseded before the linger elapsed */ }
        }, token);
    }

    private void OnActiveChanged(Repo? repo)
    {
        if (repo == null)
        {
            Update(_ => StatusBarState.Initial);
            return;
        }
        // RepoName is the registry's; Branch + ahead/behind are owned by OnPushStatus (whose
        // derivation already falls back to Repo.Branch). Don't seed Branch here — on a switch the
        // store's push status fires before this handler, and re-seeding would clobber it.
        Update(s => s with { HasActiveRepo = true, RepoName = repo.DisplayName });
    }

    private void OnPushStatus(PushStatus status)
    {
        if (!State.Value.HasActiveRepo) return;
        Update(s => s with
        {
            Branch = status.CurrentBranchName ?? s.Branch,
            HasUpstream = status.HasUpstream,
            IsDetached = status.IsDetached,
            Ahead = status.Ahead,
            Behind = status.Behind,
        });
    }

    public override void Dispose()
    {
        _feedbackCts?.Cancel();
        _feedbackCts?.Dispose();
        _feedbackCts = null;
        _updateSpinner.Dispose();
        base.Dispose();
    }
}
