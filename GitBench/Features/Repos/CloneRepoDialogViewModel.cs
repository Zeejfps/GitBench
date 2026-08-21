using GitBench.Features.Identity;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>
/// Backs <see cref="CloneRepoDialog"/>. Collects a remote URL, a parent directory to clone
/// into, the subfolder name (auto-derived from the URL until the user edits it), and the identity
/// profile the clone runs under. On a successful clone it registers and activates the new repo,
/// mirroring "Open from folder".
/// </summary>
internal sealed class CloneRepoDialogViewModel : IDialogViewModel
{
    public State<string> Url { get; } = new(string.Empty);
    public State<string> ParentDir { get; } = new(string.Empty);
    public State<string> FolderName { get; } = new(string.Empty);

    /// <summary>Explicitly chosen identity profile, or null to let the URL decide.</summary>
    public State<Guid?> ProfileId { get; } = new(null);

    /// <summary>Every profile the identity manager knows about; empty hides the picker entirely.</summary>
    public IReadOnlyList<IdentityProfile> Profiles { get; }

    /// <summary>The profile the clone will actually run under — the chosen one, else the URL match.</summary>
    public IReadable<IdentityProfile?> EffectiveProfile { get; }

    /// <summary>True while <see cref="EffectiveProfile"/> comes from the URL rather than a choice.</summary>
    public IReadable<bool> ProfileIsAutoMatched { get; }

    public AsyncCommand Clone { get; }

    public event Action? CloseRequested;

    // Tracks the last name we auto-filled so manual edits to FolderName stick: we only
    // overwrite the field while it still matches what we last derived from the URL.
    private string _lastAutoName = string.Empty;
    private string? _clonedPath;
    private string? _cloneWarning;
    private readonly Guid? _targetGroupId;

    public CloneRepoDialogViewModel(
        IGitRemoteOperations gitService,
        IRepoRegistry registry,
        IdentityProfileService profiles,
        GitIdentityService identity,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        Guid? targetGroupId = null)
    {
        _targetGroupId = targetGroupId;
        Profiles = profiles.Snapshot;

        Url.Subscribe(url =>
        {
            if (FolderName.Value != _lastAutoName) return; // user took over the field
            var derived = DeriveFolderName(url);
            FolderName.Value = derived;
            _lastAutoName = derived;
        });

        EffectiveProfile = new Derived<IdentityProfile?>(() => Resolve(profiles));
        ProfileIsAutoMatched = new Derived<bool>(() => ProfileId.Value == null && EffectiveProfile.Value != null);

        var gate = new Derived<bool>(() =>
            Url.Value.Trim().Length > 0
            && ParentDir.Value.Trim().Length > 0
            && FolderName.Value.Trim().Length > 0);

        Clone = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var target = Path.Combine(ParentDir.Value.Trim(), FolderName.Value.Trim());
                var config = Resolve(profiles) is { } profile ? LocalIdentityConfig.For(profile) : null;
                switch (gitService.Clone(Url.Value.Trim(), target, config))
                {
                    case CloneOutcome.Failed failed:
                        return failed.Message;
                    case CloneOutcome.Cloned cloned:
                        _clonedPath = cloned.RepoPath;
                        _cloneWarning = cloned.Warning;
                        break;
                }
                return null;
            },
            onSuccess: () =>
            {
                if (!string.IsNullOrEmpty(_clonedPath))
                {
                    registry.Open(_clonedPath, _targetGroupId);
                    PinChoice(registry, identity, _clonedPath);
                }
                CloseRequested?.Invoke();
                // Raised after the dialog closes and the repo is open, so it reads as "this repo is
                // here, and git said something about it" rather than as the clone having failed.
                if (_cloneWarning is { Length: > 0 } warning)
                    bus.Broadcast(new ShowOperationErrorMessage(loc.Strings.Value.ReposCloneWarningTitle, warning));
            },
            gate: gate);
    }

    private IdentityProfile? Resolve(IdentityProfileService profiles)
        => ProfileId.Value is { } id
            ? profiles.Find(id)
            : IdentityProfileMatch.ForRemoteUrl(profiles.Snapshot, Url.Value);

    // A deliberate pick has to outlive the clone: the profile that authenticated it must be the one
    // the repo fetches and pushes with, and its match rules may not cover this remote at all (that
    // being why the user picked it). An auto match needs nothing — the resolver reaches the same
    // profile on its own. Flushed by path because opening the repo can memoize a resolution before
    // the override lands.
    private void PinChoice(IRepoRegistry registry, GitIdentityService identity, string path)
    {
        if (ProfileId.Value is not { } chosen) return;
        var opened = registry.Repos.FirstOrDefault(r => PathKey.Comparer.Equals(r.Path, path));
        if (opened is null) return;
        registry.SetIdentityOverride(opened.Id, chosen);
        identity.Flush(path);
    }

    /// <summary>
    /// Derives the default destination folder from a git URL the way <c>git clone</c> does:
    /// the last path segment with any trailing <c>.git</c> removed. Handles both HTTPS
    /// (<c>https://host/user/repo.git</c>) and scp-like SSH (<c>git@host:user/repo.git</c>).
    /// </summary>
    internal static string DeriveFolderName(string url)
    {
        var u = url.Trim().TrimEnd('/', '\\');
        if (u.Length == 0) return string.Empty;
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            u = u[..^4].TrimEnd('/', '\\');

        var sep = u.LastIndexOfAny(new[] { '/', '\\', ':' });
        var name = sep >= 0 ? u[(sep + 1)..] : u;
        return name.Trim();
    }

    public void Dispose() { }
}
