using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Backs the bottom <see cref="StatusBarView"/>: the active repo name, current branch, and
/// ahead/behind counts (loaded off-thread via <see cref="IGitService.GetPushStatus"/>, the same
/// source the actions toolbar uses), plus the theme toggle. Refreshes on repo switch and on the
/// same ref/commit/working-tree messages the rest of the app reacts to.
/// </summary>
internal sealed class StatusBarViewModel : ViewModelBase<StatusBarState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly State<ThemeMode> _themeMode;
    private readonly GenerationGuard _statusGen;

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
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        State<ThemeMode> themeMode)
        : base(dispatcher, StatusBarState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _themeMode = themeMode;
        _statusGen = CreateLane();

        HasActiveRepo = Slice(s => s.HasActiveRepo);
        RepoName = Slice(s => s.RepoName);
        Branch = Slice(s => s.Branch);
        HasBranch = Slice(s => !string.IsNullOrEmpty(s.Branch));
        ShowAhead = Slice(s => HasTracking(s) && s.Ahead > 0);
        ShowBehind = Slice(s => HasTracking(s) && s.Behind > 0);
        AheadText = Slice(s => s.Ahead.ToString());
        BehindText = Slice(s => s.Behind.ToString());

        ToggleTheme = new Command(DoToggleTheme);

        Subscriptions.Add(_registry.Active.Subscribe(_ => OnRepoOrRefsChanged()));
        Subscriptions.Add(bus.SubscribeScoped<RefsChangedMessage>(_ => OnRepoOrRefsChanged()));
        Subscriptions.Add(bus.SubscribeScoped<CommitCreatedMessage>(_ => OnRepoOrRefsChanged()));
        Subscriptions.Add(bus.SubscribeScoped<WorkingTreeChangedMessage>(_ => OnRepoOrRefsChanged()));

        OnRepoOrRefsChanged();
    }

    private static bool HasTracking(StatusBarState s) => s.HasUpstream && !s.IsDetached;

    private void DoToggleTheme() =>
        _themeMode.Value = _themeMode.Value == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;

    private void OnRepoOrRefsChanged()
    {
        var repo = _registry.Active.Value;
        if (repo == null)
        {
            _statusGen.Bump();
            Update(_ => StatusBarState.Initial);
            return;
        }

        // Show what we already know immediately; refine ahead/behind from the background read.
        Update(s => s with
        {
            HasActiveRepo = true,
            RepoName = repo.DisplayName,
            Branch = repo.Branch,
        });

        RunBackground<PushStatus>(
            work: () => (_gitService.GetPushStatus(repo), null),
            onResult: (status, _) =>
            {
                if (status == null || _registry.Active.Value?.Id != repo.Id) return;
                Update(s => s with
                {
                    Branch = status.CurrentBranchName ?? s.Branch,
                    HasUpstream = status.HasUpstream,
                    IsDetached = status.IsDetached,
                    Ahead = status.Ahead,
                    Behind = status.Behind,
                });
            },
            lane: _statusGen);
    }
}
