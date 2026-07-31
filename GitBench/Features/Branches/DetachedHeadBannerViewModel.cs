using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

// Drives the repo-level detached-HEAD banner. Two shapes: a warning when commits sit on no
// branch (AtRisk — top-level repos and submodules alike), and an informational "switch onto
// <branch>" for a submodule the superproject parked on a branch tip. Mirrors
// OperationStateBannerViewModel: self-contained, always subscribed (independent of which
// main-content tab is showing), and recomputes its git state on the same change signals.
internal sealed class DetachedHeadBannerViewModel : ViewModelBase<DetachedHeadBannerState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly IRepoHeadStore _head;
    private readonly ILocalizationService _loc;

    public IReadable<bool> IsVisible { get; }
    public IReadable<bool> IsAtRisk { get; }
    public IReadable<bool> IsOnBranchTip { get; }
    public IReadable<string?> OnTipMessage { get; }
    public IReadable<string?> SwitchLabel { get; }
    public IReadable<string?> SwitchTooltip { get; }
    public Command CreateBranch { get; }
    public Command SwitchToBranch { get; }

    public DetachedHeadBannerViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        IRepoHeadStore head,
        ILocalizationService loc)
        : base(dispatcher, DetachedHeadBannerState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;
        _head = head;
        _loc = loc;

        // The banner text is bound reactively (not snapshotted at build time) so it stays correct
        // when the branch changes without the Show swapping — reading _loc.Strings.Value inside the
        // selector also makes it react to locale switches. Slices recompute in creation order (see
        // ViewModelBase.Slice), so declare these before IsVisible — the outer Show's trigger — to
        // guarantee they're current when the banner builds.
        OnTipMessage = Slice<string?>(s => _loc.Strings.Value.BranchesDetachedHeadOnTipMessage(s.Branch ?? ""));
        SwitchLabel = Slice<string?>(s => _loc.Strings.Value.BranchesDetachedHeadSwitchButton(s.Branch ?? ""));
        SwitchTooltip = Slice<string?>(s => _loc.Strings.Value.BranchesDetachedHeadSwitchTooltip(s.Branch ?? ""));
        IsAtRisk = Slice(s => s.Kind == DetachedHeadKind.AtRisk);
        IsOnBranchTip = Slice(s => s.Kind == DetachedHeadKind.OnBranchTip);
        IsVisible = Slice(s => s.Kind != DetachedHeadKind.None);
        CreateBranch = new Command(DoCreateBranch);
        SwitchToBranch = new Command(DoSwitchToBranch);

        Subscriptions.Add(_registry.Active.Subscribe(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<RefsChangedMessage>(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<CommitCreatedMessage>(_ => Reload()));
    }

    // Creates the branch at the detached commit — GitRef.Head is literally where we are. The
    // dialog's "checkout after create" box defaults on, so the common flow captures the commits onto
    // a branch and lands the user on it (clearing this banner).
    private void DoCreateBranch()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new CreateBranchDialog
        {
            Repo = repo,
            StartPoint = GitRef.Head,
            StartPointLabel = "HEAD",
            OnClose = onClose,
        }));
    }

    // Attaches the detached submodule onto the branch tip it's sitting on (checkout, or
    // fast-forward/create as needed) — no dialog, since there's a single obvious target.
    private void DoSwitchToBranch()
    {
        var repo = _registry.Active.Value;
        var branch = State.Value.Branch;
        if (repo == null || string.IsNullOrEmpty(branch)) return;

        // Attaching lands HEAD on a branch, so it's a branch switch as far as every other component
        // is concerned. Run through the head store rather than this view model's background helpers:
        // those are load semantics and drop their continuation once the VM is disposed, which for a
        // mutation would lose the error, the refresh, and the settle that ends the declaration.
        var service = _gitService;
        _head.RunMove(repo, branch, () => service.AttachDetachedHead(repo, branch));
    }

    private void Reload()
    {
        var repo = _registry.Active.Value;
        if (repo == null)
        {
            Update(_ => DetachedHeadBannerState.Initial);
            return;
        }

        var repoId = repo.Id;
        var service = _gitService;
        RunBackground<DetachedHeadReport>(
            () =>
            {
                try { return (service.GetDetachedHeadReport(repo), null); }
                catch { return (DetachedHeadReport.None, null); }
            },
            (report, _) =>
            {
                if (_registry.Active.Value?.Id != repoId) return;
                report ??= DetachedHeadReport.None;
                Update(_ => new DetachedHeadBannerState(report.Kind, report.Branch));
            });
    }
}

internal sealed record DetachedHeadBannerState(DetachedHeadKind Kind, string? Branch)
{
    public static DetachedHeadBannerState Initial { get; } = new(DetachedHeadKind.None, null);
}
