using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Observable;

namespace GitBench.Features.Commits;

/// <summary>
/// One open file tab in the commit-details surface: a file path pinned to a commit, with its own
/// <see cref="DiffViewModel"/> so each tab keeps its loaded diff, scroll position, and full-file
/// toggle independently of the others. Created when a file is opened and disposed when its tab is
/// closed or the selected commit changes.
/// </summary>
internal sealed class CommitFileTab : IDisposable
{
    /// <summary>The <see cref="Sha"/> stand-in for a working-tree file, which has no commit identity.</summary>
    public const string WorkingTreeSha = "working-tree";

    /// <summary>The working tree's stand-in for a commit sha, scoped per repo so a surface keyed on it
    /// treats a repo switch as new content rather than a refresh of the same list.</summary>
    public static string WorkingTreeKey(Guid repoId) => $"{WorkingTreeSha}:{repoId:N}";

    private readonly State<DiffTarget?> _target;

    public string Path { get; }
    public string FileName { get; }
    public string Sha { get; }
    public DiffViewModel Diff { get; }

    /// <summary>A tab over a file's whole working-tree change (HEAD→disk), for the working-tree
    /// review surface. Staging the file never re-targets it.</summary>
    public static CommitFileTab ForWorkingTree(
        string path,
        Guid repoId,
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        IPlatformShell? shell = null)
        => new(path, repoId, registry, gitService, dispatcher, bus, loc, shell);

    private CommitFileTab(
        string path,
        Guid repoId,
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        IPlatformShell? shell)
    {
        Path = path;
        FileName = LastSegment(path);
        Sha = WorkingTreeSha;
        _target = new State<DiffTarget?>(new DiffTarget(path, DiffSide.WorkingTree));
        Diff = new DiffViewModel(_target, registry, gitService, dispatcher, bus, shell, loc: loc, pinnedRepoId: repoId);
    }

    public CommitFileTab(
        string path,
        string sha,
        Guid repoId,
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        string? baseSha = null)
    {
        Path = path;
        FileName = LastSegment(path);
        // Combined range tab (baseSha set): the diff is base→head and the reviewed-state identity is
        // the shared range key, kept distinct from the tip commit's own sha so marks never collide.
        Sha = baseSha == null ? sha : ReviewFileKey.ForRange(baseSha, sha);
        _target = new State<DiffTarget?>(baseSha == null
            ? new DiffTarget(path, DiffSide.Commit, sha)
            : new DiffTarget(path, DiffSide.Range, sha, baseSha));
        Diff = new DiffViewModel(_target, registry, gitService, dispatcher, bus, loc: loc, pinnedRepoId: repoId);
    }

    public void Dispose()
    {
        Diff.Dispose();
        _target.Dispose();
    }

    private static string LastSegment(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }
}
