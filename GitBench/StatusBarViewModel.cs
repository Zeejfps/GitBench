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
    private readonly IRepoRegistry _registry;
    private readonly State<ThemeMode> _themeMode;

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

    public StatusBarViewModel(
        IRepoRegistry registry,
        IUiDispatcher dispatcher,
        IRepoSnapshotStore store,
        State<ThemeMode> themeMode)
        : base(dispatcher, StatusBarState.Initial)
    {
        _registry = registry;
        _themeMode = themeMode;

        HasActiveRepo = Slice(s => s.HasActiveRepo);
        RepoName = Slice(s => s.RepoName);
        Branch = Slice(s => s.Branch);
        HasBranch = Slice(s => !string.IsNullOrEmpty(s.Branch));
        ShowAhead = Slice(s => HasTracking(s) && s.Ahead > 0);
        ShowBehind = Slice(s => HasTracking(s) && s.Behind > 0);
        AheadText = Slice(s => s.Ahead.ToString());
        BehindText = Slice(s => s.Behind.ToString());

        ToggleTheme = new Command(DoToggleTheme);

        // Repo name / has-repo come from the registry (instant, non-git); branch + ahead/behind
        // are refined from the store's push status. Both subscriptions fire immediately.
        Subscriptions.Add(_registry.Active.Subscribe(OnActiveChanged));
        Subscriptions.Add(store.PushStatus.Subscribe(OnPushStatus));
    }

    private static bool HasTracking(StatusBarState s) => s.HasUpstream && !s.IsDetached;

    private void DoToggleTheme() =>
        _themeMode.Value = _themeMode.Value == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;

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
}
