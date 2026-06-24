using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>
/// Backs <see cref="CloneRepoDialog"/>. Collects a remote URL, a parent directory to clone
/// into, and the subfolder name (auto-derived from the URL until the user edits it). On a
/// successful clone it registers and activates the new repo, mirroring "Open from folder".
/// </summary>
internal sealed class CloneRepoDialogViewModel : IDialogViewModel
{
    public State<string> Url { get; } = new(string.Empty);
    public State<string> ParentDir { get; } = new(string.Empty);
    public State<string> FolderName { get; } = new(string.Empty);

    public AsyncCommand Clone { get; }

    public event Action? CloseRequested;

    // Tracks the last name we auto-filled so manual edits to FolderName stick: we only
    // overwrite the field while it still matches what we last derived from the URL.
    private string _lastAutoName = string.Empty;
    private string? _clonedPath;
    private readonly Guid? _targetGroupId;

    public CloneRepoDialogViewModel(
        IGitService gitService,
        IRepoRegistry registry,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        Guid? targetGroupId = null)
    {
        _targetGroupId = targetGroupId;

        Url.Subscribe(url =>
        {
            if (FolderName.Value != _lastAutoName) return; // user took over the field
            var derived = DeriveFolderName(url);
            FolderName.Value = derived;
            _lastAutoName = derived;
        });

        var gate = new Derived<bool>(() =>
            Url.Value.Trim().Length > 0
            && ParentDir.Value.Trim().Length > 0
            && FolderName.Value.Trim().Length > 0);

        Clone = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var target = Path.Combine(ParentDir.Value.Trim(), FolderName.Value.Trim());
                switch (gitService.Clone(Url.Value.Trim(), target))
                {
                    case CloneOutcome.Failed failed:
                        return failed.Message;
                    case CloneOutcome.Cloned cloned:
                        _clonedPath = cloned.RepoPath;
                        break;
                }
                return null;
            },
            onSuccess: () =>
            {
                if (!string.IsNullOrEmpty(_clonedPath))
                    registry.Open(_clonedPath, _targetGroupId);
                CloseRequested?.Invoke();
            },
            gate: gate);
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
