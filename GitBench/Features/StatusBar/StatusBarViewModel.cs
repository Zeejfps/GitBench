using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Identity;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Backs the bottom <see cref="StatusBarView"/>: the active repo name, current branch, and
/// ahead/behind counts, plus the theme toggle. The repo name comes straight from the registry;
/// the branch and ahead/behind are projected from <see cref="IRepoStatusStore.Active"/>, so there's
/// no separate git query here.
/// </summary>
internal sealed class StatusBarViewModel : ViewModelBase<StatusBarState>
{
    // How long a manual check's "up to date" / "failed" result lingers before clearing itself.
    private const int FeedbackLingerMs = 4000;

    private readonly IRepoRegistry _registry;
    private readonly GitIdentityService _identity;
    private readonly IdentityProfileService _profiles;
    private readonly IGitService _git;
    private readonly IMessageBus _bus;
    private readonly State<ThemeMode> _themeMode;
    private readonly UpdateService _updateService;
    private readonly ILocalizationService _loc;
    private readonly State<Locale> _locale;
    private readonly SpinnerAnimation _updateSpinner;
    private readonly GenerationGuard _identityLane;
    private CancellationTokenSource? _feedbackCts;

    public IReadable<bool> HasActiveRepo { get; }
    public IReadable<string?> RepoName { get; }
    public IReadable<string?> Branch { get; }
    public IReadable<bool> HasBranch { get; }
    public IReadable<bool> ShowAhead { get; }
    public IReadable<bool> ShowBehind { get; }
    public IReadable<string> AheadText { get; }
    public IReadable<string> BehindText { get; }
    public IReadable<bool> ShowIdentity { get; }
    public IReadable<string> IdentityText { get; }
    public IReadable<string> IdentityGlyph { get; }

    public Command ToggleTheme { get; }
    public IReadable<ThemeMode> Theme => _themeMode;
    public IReadable<Locale> ActiveLocale => _locale;

    public Command CheckForUpdates { get; }
    public IReadable<bool> IsCheckingUpdates => _updateService.IsChecking;
    public IReadable<float> UpdateIconRotation => _updateSpinner.Rotation;
    public IReadable<string?> UpdateCheckFeedback => _updateService.CheckFeedback;

    public StatusBarViewModel(
        IRepoRegistry registry,
        IUiDispatcher dispatcher,
        IFrameTicker ticker,
        IRepoStatusStore status,
        GitIdentityService identity,
        IdentityProfileService profiles,
        IGitService git,
        IMessageBus bus,
        State<ThemeMode> themeMode,
        UpdateService updateService,
        ILocalizationService loc,
        State<Locale> locale)
        : base(dispatcher, StatusBarState.Initial)
    {
        _registry = registry;
        _identity = identity;
        _profiles = profiles;
        _git = git;
        _bus = bus;
        _themeMode = themeMode;
        _updateService = updateService;
        _loc = loc;
        _locale = locale;
        _updateSpinner = new SpinnerAnimation(ticker);
        _identityLane = CreateLane();

        HasActiveRepo = Slice(s => s.HasActiveRepo);
        RepoName = Slice(s => s.RepoName);
        Branch = Slice(s => s.Branch);
        HasBranch = Slice(s => !string.IsNullOrEmpty(s.Branch));
        ShowAhead = Slice(s => HasTracking(s) && s.Ahead > 0);
        ShowBehind = Slice(s => HasTracking(s) && s.Behind > 0);
        AheadText = Slice(s => s.Ahead.ToString());
        BehindText = Slice(s => s.Behind.ToString());
        ShowIdentity = Slice(s => s.HasActiveRepo && !string.IsNullOrEmpty(s.IdentityText));
        IdentityText = Slice(s => s.IdentityText ?? string.Empty);
        IdentityGlyph = Slice(s => s.IdentityIsWarning ? LucideIcons.TriangleAlert : LucideIcons.PencilLine);

        ToggleTheme = new Command(DoToggleTheme);
        CheckForUpdates = new Command(DoCheckForUpdates);

        // Re-resolve the active repo's identity whenever profiles or refs change (the resolver
        // flushes its memo on those, then raises Changed).
        _identity.Changed += OnIdentityChanged;
        Subscriptions.Add(() => _identity.Changed -= OnIdentityChanged);

        // Spin the check button's icon for the duration of a check; fires immediately with the
        // current (idle) value, leaving the spinner stopped until a check starts.
        Subscriptions.Add(_updateService.IsChecking.Subscribe(OnCheckingChanged));
        // A finished manual check's result clears itself after a short linger.
        Subscriptions.Add(_updateService.CheckFeedback.Subscribe(OnFeedbackChanged));

        // Repo name / has-repo come from the registry (instant, non-git); branch + ahead/behind
        // are refined from the status store. Both subscriptions fire immediately.
        Subscriptions.Add(_registry.Active.Subscribe(OnActiveChanged));
        Subscriptions.Add(status.Active.Subscribe(OnStatus));
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
        // RepoName is the registry's; Branch + ahead/behind are owned by OnStatus. Don't seed Branch
        // here — on a switch the status store's Active fires alongside this handler, and re-seeding
        // would clobber it.
        // Seed Branch from the registry's cached value so the bar paints instantly; OnStatus then
        // refines it (and ahead/behind) once the async probe lands.
        Update(s => s with { HasActiveRepo = true, RepoName = repo.DisplayName, Branch = repo.Branch });
        ResolveIdentity(repo.Path);
    }

    private void OnIdentityChanged()
    {
        var repo = _registry.Active.Value;
        if (repo != null) ResolveIdentity(repo.Path);
    }

    // First resolution for a repo runs a couple of git reads, so resolve off the UI thread and
    // post the label back. The identity lane drops a stale result when the active repo switches,
    // a newer resolve starts, or the VM is disposed — so out-of-order completions can't clobber
    // the current label.
    private void ResolveIdentity(string repoPath)
        => RunBackground<(string? Text, bool Warning)>(
            () => (LabelFor(_identity.Resolve(repoPath)), (string?)null),
            (label, _) =>
            {
                if (label is { } l) Update(s => s with { IdentityText = l.Text, IdentityIsWarning = l.Warning });
            },
            _identityLane);

    private (string? Text, bool Warning) LabelFor(Identity.Identity id) => id switch
    {
        Identity.Identity.FromProfile p => (p.Profile.DisplayName, false),
        Identity.Identity.FromConfig c => (FormatLocal(c), false),
        Identity.Identity.Unmatched => (_loc.Strings.Value.StatusbarIdentityNone, true),
        _ => (null, false), // NoRemote / Pending: nothing to show
    };

    private static string? FormatLocal(Identity.Identity.FromConfig c)
        => c.UserEmail != null
            ? (c.UserName != null ? $"{c.UserName} <{c.UserEmail}>" : c.UserEmail)
            : c.UserName;

    // Each selectable locale paired with its endonym (the language's own name — script-neutral, so
    // not itself localized). Pseudo is the dev/layout-test locale, kept here for parity with the
    // native macOS Language menu.
    private static readonly (Locale Locale, string Name)[] LanguageOptions =
    {
        (Locale.En, "English"),
        (Locale.Es, "Español"),
        (Locale.Ja, "日本語"),
        (Locale.ZhHans, "简体中文"),
        (Locale.Ko, "한국어"),
        (Locale.Ar, "العربية"),
        (Locale.Pseudo, "Pseudo"),
    };

    // Compact code shown on the status-bar chip (the menu carries the full endonyms).
    public static string Code(Locale locale) => locale switch
    {
        Locale.En => "EN",
        Locale.Es => "ES",
        Locale.Ja => "JA",
        Locale.ZhHans => "ZH",
        Locale.Ko => "KO",
        Locale.Ar => "AR",
        Locale.Pseudo => "PS",
        _ => "EN",
    };

    // The language picker — the cross-platform way to switch locale (the native macOS menu carries
    // the same items but exists only on macOS). A checkmark marks the active locale.
    public IReadOnlyList<RepoBarContextMenu.Item> BuildLanguageMenu()
    {
        var active = _locale.Value;
        var items = new List<RepoBarContextMenu.Item>(LanguageOptions.Length);
        foreach (var (locale, name) in LanguageOptions)
        {
            var target = locale;
            items.Add(new RepoBarContextMenu.Item(
                name,
                () => _locale.Value = target,
                Icon: active == locale ? LucideIcons.CheckSquare : null));
        }

        return items;
    }

    // Built fresh on each chip click so it reflects the current profiles + resolution.
    public IReadOnlyList<RepoBarContextMenu.Item> BuildIdentityMenu()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

        // Read the already-resolved identity rather than resolving here, which would block the UI
        // thread on git during the click. Null until the first resolve lands.
        var resolved = _identity.Cached(repo.Path);
        var activeProfileId = (resolved as Identity.Identity.FromProfile)?.Profile.Id;
        var s = _loc.Strings.Value;
        var items = new List<RepoBarContextMenu.Item>();

        foreach (var p in _profiles.Profiles)
        {
            var profile = p;
            items.Add(new RepoBarContextMenu.Item(
                profile.DisplayName,
                () => ApplyOverride(repo, profile.Id),
                Icon: activeProfileId == profile.Id ? LucideIcons.CheckSquare : null));
        }

        if (_profiles.Profiles.Count > 0) items.Add(RepoBarContextMenu.Separator);

        items.Add(new RepoBarContextMenu.Item(
            s.StatusbarIdentityAutoDetect,
            () => ApplyOverride(repo, null),
            Enabled: _registry.GetIdentityOverride(repo.Id) != null));

        if (resolved is Identity.Identity.FromProfile pinned)
            items.Add(new RepoBarContextMenu.Item(s.StatusbarIdentityPinToRepo, () => Pin(repo, pinned.Profile.Id)));

        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            s.StatusbarIdentityAddProfile,
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new IdentityProfileEditDialog { Existing = null, OnClose = onClose }))));

        if (activeProfileId is { } editId && _profiles.Find(editId) is { } editable)
        {
            items.Add(new RepoBarContextMenu.Item(
                s.StatusbarIdentityEditProfile(editable.DisplayName),
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new IdentityProfileEditDialog { Existing = editable, OnClose = onClose }))));
            items.Add(new RepoBarContextMenu.Item(
                s.StatusbarIdentityDeleteProfile(editable.DisplayName),
                () => _profiles.Remove(editable.Id)));
        }

        return items;
    }

    // Pin/clear a manual override and flush the resolver memo so the chip and injected args refresh.
    private void ApplyOverride(Repo repo, Guid? profileId)
    {
        _registry.SetIdentityOverride(repo.Id, profileId);
        _identity.FlushAll();
    }

    // A pin is a deliberate mutation on a specific repo, so its continuation must always apply
    // (clearing the override even if the user has since switched repos) — hence a plain post
    // rather than the lane-guarded ResolveIdentity path, which is meant to drop stale labels.
    private void Pin(Repo repo, Guid profileId)
    {
        var profile = _profiles.Find(profileId);
        if (profile == null) return;
        var config = LocalIdentityConfig.For(profile);
        var dispatcher = Dispatcher;
        var strings = _loc.Strings.Value;
        Task.Run(() =>
        {
            var outcome = _git.PinLocalIdentity(repo, config);
            dispatcher.Post(() =>
            {
                // The pin wrote --local config, so clear the manual override and let the resolver
                // honor that config (inject nothing) — otherwise the override would keep injecting
                // and GUI/terminal could diverge.
                if (outcome is GitOutcome.Success) _registry.SetIdentityOverride(repo.Id, null);
                _identity.FlushAll();
                if (outcome is GitOutcome.Failed failed)
                    _bus.Broadcast(new ShowOperationErrorMessage(strings.StatusbarErrorPinIdentity, failed.Message));
            });
        });
    }

    private void OnStatus(RepoStatus status)
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
