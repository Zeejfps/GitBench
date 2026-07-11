using GitBench.Features.Diff;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// The cross-repo working-tree review's <see cref="IReviewedFileTracker"/> (Phase 5.2): the N-repo twin
/// of <c>StagedFileTracker</c>. A file's "mark" is its staged state, keyed by repo-qualified path; the
/// git index (per member) is the source of truth, so a mark survives a restart. Checking a box resolves
/// the qualified path back to its owning member and routes the stage/unstage to that repo — so the same
/// checkbox stages in whichever repo the file lives in (Locked decision #4: git-facing calls always get
/// the bare path plus the member). A partially staged file (staged, then edited again) is the
/// indeterminate mark, kept per member exactly as the single-repo surface does.
/// </summary>
internal sealed class ChangeSetStagedFileTracker : IReviewedFileTracker, IDisposable
{
    private static readonly IReadOnlySet<string> NoPaths = new HashSet<string>();
    private static readonly IReadOnlyDictionary<string, (Guid, string)> NoResolver =
        new Dictionary<string, (Guid, string)>();

    // Applies a stage / unstage of the given bare paths to one member repo. Supplied by the host, which
    // owns the git call + threading and the post-op WorkingTreeChangedMessage.
    private readonly Action<Guid, IReadOnlyList<string>> _stage;
    private readonly Action<Guid, IReadOnlyList<string>> _unstage;
    private readonly State<int> _revision = new(0);

    private IReadOnlyDictionary<string, (Guid RepoId, string Path)> _resolver = NoResolver;
    private IReadOnlySet<string> _fullyStaged = NoPaths;
    private IReadOnlySet<string> _partlyStaged = NoPaths;

    public ChangeSetStagedFileTracker(
        Action<Guid, IReadOnlyList<string>> stage, Action<Guid, IReadOnlyList<string>> unstage)
    {
        _stage = stage;
        _unstage = unstage;
    }

    public IReadable<int> Revision => _revision;

    public bool IsViewed(string path) => _fullyStaged.Contains(path);

    /// <summary>The indeterminate mark: the file has staged content and further unstaged edits on top.</summary>
    public bool IsPartiallyStaged(string path) => _partlyStaged.Contains(path);

    /// <summary>Whether any of the file's content is staged — the paths "Unstage" can act on.</summary>
    public bool HasStagedContent(string path) => _fullyStaged.Contains(path) || _partlyStaged.Contains(path);

    public void ToggleViewed(string path) => SetViewed([path], !IsViewed(path));

    /// <summary>
    /// Stages / unstages the given qualified paths, grouping the request by member and skipping paths
    /// already in the requested state, then routing each member's bare-path batch to its repo. The
    /// index ops move files between staged / unstaged, whose <c>WorkingTreeChangedMessage</c> lands a
    /// fresh snapshot that <see cref="SetState"/> adopts — so the marks refresh from git, not optimistically.
    /// </summary>
    public void SetViewed(IReadOnlyList<string> paths, bool viewed)
    {
        if (paths.Count == 0) return;
        var plan = ChangeSetWorkingTreeAggregator.PlanStage(paths, viewed, _fullyStaged, _partlyStaged, _resolver);
        if (plan.Count == 0) return;
        foreach (var (repoId, bare) in plan)
        {
            if (viewed) _stage(repoId, bare);
            else _unstage(repoId, bare);
        }
    }

    /// <summary>Adopts a freshly aggregated working tree: the resolver + the per-qualified-path staged
    /// state. The bump refreshes every bound checkbox.</summary>
    public void SetState(
        IReadOnlyDictionary<string, (Guid RepoId, string Path)> resolver,
        IReadOnlySet<string> fullyStaged,
        IReadOnlySet<string> partlyStaged)
    {
        _resolver = resolver;
        _fullyStaged = fullyStaged;
        _partlyStaged = partlyStaged;
        _revision.Value++;
    }

    public void Dispose() => _revision.Dispose();
}
