using GitBench.Features.ChangeSets;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// Implementation #4 of <see cref="IReviewSurfaceModel"/> (Phase 5): the Changes panel's cross-repo
/// mode. When the active repo's checked-out branch is a synced set branch, this aggregates the working
/// trees of every member that also has that branch checked out (5.3) into one review surface — one file
/// tree grouped by repo, one stacked diff, one progress meter — driven by the shared
/// <see cref="CommitDetailsViewModel.ShowWorkingTrees"/>. A file's mark is its staged state
/// (<see cref="ReviewMarkKind.Staged"/>); checking it stages the bare path in the owning member repo
/// (Locked decision #4), partial-staging kept as the indeterminate mark. The commit box batch-commits one
/// message per member with staged changes, each carrying the <c>Change-Set</c> trailer (5.4).
///
/// A singleton (like <see cref="LocalChanges.WorkingTreeReviewViewModel"/>): it tracks the live active
/// repo + set membership rather than being pinned, so toggling into "All repos" always reflects the
/// current set. Membership recomputes on active-repo / ref / detection changes; each member's working
/// tree reloads on its <see cref="WorkingTreeChangedMessage"/>.
/// </summary>
internal sealed class ChangeSetWorkingTreeReviewViewModel : IReviewSurfaceModel, IDisposable
{
    private static readonly IReadOnlyList<FileChange> EmptyFiles = Array.Empty<FileChange>();

    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly SyncedBranchIndex _index;
    private readonly IRepoStatusStore _status;
    private readonly ChangeSetOperations _ops;
    private readonly CommitDetailsViewModel _details;
    private readonly ChangeSetStagedFileTracker _marks;
    private readonly ReviewFileCursor _cursor;

    // Current membership: the set's members that have the branch checked out, in group order.
    private sealed record Member(Guid RepoId, Repo Repo);
    private List<Member> _members = new();
    private HashSet<Guid> _memberIds = new();
    private IReadOnlyDictionary<Guid, string> _repoKeys = new Dictionary<Guid, string>();
    private readonly Dictionary<Guid, (IReadOnlyList<FileChange> Unstaged, IReadOnlyList<FileChange> Staged)> _localChanges = new();
    private string _branch = string.Empty;

    private readonly State<bool> _available = new(false);
    private readonly State<string> _branchName = new(string.Empty);
    private readonly State<string> _commitTitle = new(string.Empty);
    private readonly State<bool> _commitBusy = new(false);
    private readonly State<bool> _cheatsheetOpen = new(false);

    private readonly Derived<ReviewHud> _hud;
    private readonly Derived<float> _filesFraction;
    private readonly Derived<string> _filesStagedLabel;
    private readonly Derived<bool> _hasFiles;
    private readonly Derived<bool> _canStageSelected;
    private readonly Derived<bool> _canUnstageSelected;
    private readonly Derived<bool> _commitEnabled;
    private readonly List<IDisposable> _subscriptions = new();

    private int _generation;
    private bool _disposed;

    public ChangeSetWorkingTreeReviewViewModel(
        IRepoRegistry registry,
        IGitService git,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        SyncedBranchIndex index,
        IRepoStatusStore status,
        ChangeSetOperations ops,
        CommitDetailsViewModel details)
    {
        _registry = registry;
        _git = git;
        _dispatcher = dispatcher;
        _bus = bus;
        _loc = loc;
        _index = index;
        _status = status;
        _ops = ops;
        _details = details;
        _marks = new ChangeSetStagedFileTracker(DoStage, DoUnstage);
        _cursor = new ReviewFileCursor(Files, _marks);

        _hud = new Derived<ReviewHud>(BuildHud);
        _filesFraction = new Derived<float>(() => _hud.Value.FilesFraction);
        _filesStagedLabel = new Derived<string>(BuildFilesStagedLabel);
        _hasFiles = new Derived<bool>(() => Files().Count > 0);
        _canStageSelected = new Derived<bool>(() => AnySelected(p => !_marks.IsViewed(p)));
        _canUnstageSelected = new Derived<bool>(() => AnySelected(_marks.HasStagedContent));
        _commitEnabled = new Derived<bool>(() =>
            !_commitBusy.Value && _commitTitle.Value.Trim().Length > 0 && AnyStagedAnywhere());
        StageSelected = new Command(() => SetSelectedStaged(true), _canStageSelected);
        UnstageSelected = new Command(() => SetSelectedStaged(false), _canUnstageSelected);
        StageAll = new Command(() => SetAllStaged(true));
        UnstageAll = new Command(() => SetAllStaged(false));
        Commit = new Command(DoCommit, _commitEnabled);

        // Membership follows the live active repo, ref changes (checkouts move members in/out), and
        // detection changes; each member's working tree reloads on its own change signal.
        _subscriptions.Add(_registry.Active.Subscribe(_ => RecomputeMembership()));
        _subscriptions.Add(_bus.SubscribeScoped<RefsChangedMessage>(_ => RecomputeMembership()));
        _subscriptions.Add(_index.Revision.Subscribe(_ => RecomputeMembership()));
        _subscriptions.Add(_bus.SubscribeScoped<WorkingTreeChangedMessage>(m =>
        {
            if (_memberIds.Contains(m.RepoId)) ReloadMember(m.RepoId);
        }));
        _subscriptions.Add(_bus.SubscribeScoped<CommitCreatedMessage>(m =>
        {
            if (_memberIds.Contains(m.RepoId)) ReloadMember(m.RepoId);
        }));
    }

    public ReviewMarkKind MarkKind => ReviewMarkKind.Staged;
    public IReviewedFileTracker ReviewedFiles => _marks;
    public CommitDetailsViewModel Details => _details;

    /// <summary>Whether the active repo's branch is a synced set with two or more members checked out —
    /// gates the Changes panel's "All repos" toggle and this surface.</summary>
    public IReadable<bool> IsAvailable => _available;

    /// <summary>The set branch name, for the "All repos on &lt;branch&gt;" toggle label + the trailer.</summary>
    public IReadable<string> BranchName => _branchName;

    public IReadable<string?> ActiveFile => _cursor.ActiveFile;
    public IReadable<IReadOnlySet<string>> SelectedPaths => _cursor.SelectedPaths;
    public IReadable<string?> SelectionCursor => _cursor.SelectionCursor;
    public IReadable<ReviewHud> Hud => _hud;
    public IReadable<bool> CheatsheetOpen => _cheatsheetOpen;
    public IReadable<float> FilesFraction => _filesFraction;
    public IReadable<string> FilesStagedLabel => _filesStagedLabel;
    public IReadable<bool> HasFiles => _hasFiles;

    public Command StageSelected { get; }
    public Command UnstageSelected { get; }
    public Command StageAll { get; }
    public Command UnstageAll { get; }

    // Commit box (5.4): one message committed per member with staged changes, with the Change-Set trailer.
    public IReadable<string> CommitTitle => _commitTitle;
    public IReadable<bool> CommitBusy => _commitBusy;
    public IReadable<bool> CommitEnabled => _commitEnabled;
    public Command Commit { get; }
    public void SetCommitTitle(string value) => _commitTitle.Value = value;

    public event Action<string>? ScrollToFileRequested
    {
        add => _cursor.ScrollToFileRequested += value;
        remove => _cursor.ScrollToFileRequested -= value;
    }

    public bool IsFileViewed(string path) => _marks.IsViewed(path);
    public bool IsFilePartiallyMarked(string path) => _marks.IsPartiallyStaged(path);
    public void ToggleFileViewed(string path) => _marks.ToggleViewed(path);
    public void ToggleActiveFileViewed() => _cursor.ToggleActiveFileMarked();

    public void ReportActiveFile(string path) => _cursor.ReportActiveFile(path);
    public void ActivateFile(string path) => _cursor.ActivateFile(path);

    public void SelectFile(string path, InputModifiers modifiers, IReadOnlyList<string> visiblePaths)
        => _cursor.SelectFile(path, modifiers, visiblePaths);

    public void SelectAllFiles(IReadOnlyList<string> visiblePaths) => _cursor.SelectAllFiles(visiblePaths);

    public void NextFile() => _cursor.NextFile();
    public void PrevFile() => _cursor.PrevFile();

    public void ToggleCheatsheet() => _cheatsheetOpen.Value = !_cheatsheetOpen.Value;
    public void CloseCheatsheet() => _cheatsheetOpen.Value = false;

    public IReadOnlyList<RepoBarContextMenu.Item> BuildFileContextMenuItems(string path)
        => BuildStageContextMenuItems(_cursor.ResolveTargetPaths(path));

    public IReadOnlyList<RepoBarContextMenu.Item> BuildFolderContextMenuItems(IReadOnlyList<string> paths)
        => BuildStageContextMenuItems(paths);

    private IReadOnlyList<RepoBarContextMenu.Item> BuildStageContextMenuItems(IReadOnlyList<string> targets)
    {
        var s = _loc.Strings.Value;
        var toStage = new List<string>(targets.Count);
        var toUnstage = new List<string>(targets.Count);
        foreach (var p in targets)
        {
            if (!_marks.IsViewed(p)) toStage.Add(p);
            if (_marks.HasStagedContent(p)) toUnstage.Add(p);
        }

        var items = new List<RepoBarContextMenu.Item>(2);
        if (toStage.Count > 0)
            items.Add(new RepoBarContextMenu.Item(
                s.ReviewContextStage(toStage.Count), () => _marks.SetViewed(toStage, true)));
        if (toUnstage.Count > 0)
            items.Add(new RepoBarContextMenu.Item(
                s.ReviewContextUnstage(toUnstage.Count), () => _marks.SetViewed(toUnstage, false)));
        return items;
    }

    // ---- membership ----

    private void RecomputeMembership()
    {
        if (_disposed) return;
        var members = ComputeMembers(out var branch);
        if (members.Count < 2)
        {
            if (_members.Count > 0)
            {
                _members = new List<Member>();
                _memberIds = new HashSet<Guid>();
                _localChanges.Clear();
                _details.Clear();
            }
            _branch = string.Empty;
            _branchName.Value = string.Empty;
            _available.Value = false;
            return;
        }

        var ids = members.Select(m => m.RepoId).ToHashSet();
        _branch = branch;
        _branchName.Value = branch;
        _available.Value = true;
        if (ids.SetEquals(_memberIds)) return; // same set — data still valid, no rebuild

        _members = members;
        _memberIds = ids;
        var named = new List<(Guid, string)>(members.Count);
        foreach (var m in members) named.Add((m.RepoId, m.Repo.DisplayName));
        _repoKeys = RepoQualifiedPaths.BuildKeys(named);
        _localChanges.Clear();
        ReloadAll();
    }

    // The set's members that have the branch checked out (5.3): the synced-branch members whose current
    // branch equals the active repo's branch. Fewer than two → not an active cross-repo surface.
    private List<Member> ComputeMembers(out string branch)
    {
        branch = string.Empty;
        var active = _registry.Active.Value;
        if (active is not { IsPrimary: true }) return new List<Member>();
        var current = _status.For(active.Id).CurrentBranchName;
        if (string.IsNullOrEmpty(current)) return new List<Member>();

        var synced = _index.SyncedReposFor(active.Id, current);
        if (synced.Count < 2) return new List<Member>();

        var members = new List<Member>(synced.Count);
        foreach (var id in synced)
        {
            if (!string.Equals(_status.For(id).CurrentBranchName, current, StringComparison.Ordinal)) continue;
            if (FindRepo(id) is { } repo) members.Add(new Member(id, repo));
        }
        if (members.Count < 2) return new List<Member>();
        branch = current;
        return members;
    }

    // ---- working-tree loads ----

    private void ReloadAll()
    {
        var gen = ++_generation;
        var members = _members.ToList();
        RunBackground(
            () =>
            {
                var loaded = new List<(Guid, (IReadOnlyList<FileChange>, IReadOnlyList<FileChange>))>(members.Count);
                foreach (var m in members) loaded.Add((m.RepoId, LoadChanges(m.Repo)));
                return loaded;
            },
            loaded =>
            {
                if (gen != _generation) return;
                foreach (var (id, lists) in loaded) _localChanges[id] = lists;
                Rebuild();
            });
    }

    private void ReloadMember(Guid repoId)
    {
        if (FindRepo(repoId) is not { } repo) return;
        var gen = _generation;
        RunBackground(
            () => LoadChanges(repo),
            lists =>
            {
                if (gen != _generation || !_memberIds.Contains(repoId)) return;
                _localChanges[repoId] = lists;
                Rebuild();
            });
    }

    private (IReadOnlyList<FileChange> Unstaged, IReadOnlyList<FileChange> Staged) LoadChanges(Repo repo)
    {
        try
        {
            if (_git.GetLocalChanges(repo) is Fetched<LocalChangesSnapshot>.Ok ok)
                return (ok.Value.Unstaged, ok.Value.Staged);
        }
        catch { /* fold to empty below */ }
        return (EmptyFiles, EmptyFiles);
    }

    // Projects the cached per-member working trees into the shared details surface (grouped by repo) and
    // re-seeds the marks tracker with the aggregated staged state — the working-tree twin of
    // ChangeSetReviewViewModel.DriveDetails.
    private void Rebuild()
    {
        var raw = new List<MemberWorkingTree>(_members.Count);
        var sections = new List<DetailsWorkingTreeSection>(_members.Count);
        foreach (var m in _members)
        {
            var key = _repoKeys.TryGetValue(m.RepoId, out var k) ? k : m.RepoId.ToString("N");
            var lists = _localChanges.TryGetValue(m.RepoId, out var lc) ? lc : (EmptyFiles, EmptyFiles);
            raw.Add(new MemberWorkingTree(m.RepoId, key, lists.Item1, lists.Item2));
            var merged = ChangeSetWorkingTreeAggregator.MergeStagedWins(lists.Item1, lists.Item2);
            sections.Add(new DetailsWorkingTreeSection.Files(m.RepoId, key, merged));
        }

        var agg = ChangeSetWorkingTreeAggregator.Aggregate(raw);
        _details.ShowWorkingTrees(sections);
        _marks.SetState(agg.Resolver, agg.FullyStaged, agg.PartlyStaged);
        _cursor.OnFilesLoaded(Files());
    }

    // ---- staging ----

    private void DoStage(Guid repoId, IReadOnlyList<string> bare) => RunIndexOp(repoId, bare, stage: true);
    private void DoUnstage(Guid repoId, IReadOnlyList<string> bare) => RunIndexOp(repoId, bare, stage: false);

    private void RunIndexOp(Guid repoId, IReadOnlyList<string> bare, bool stage)
    {
        if (bare.Count == 0 || FindRepo(repoId) is not { } repo) return;
        Task.Run(() =>
        {
            try { _ = stage ? _git.Stage(repo, bare) : _git.Unstage(repo, bare); }
            catch { /* the reload below re-reads git either way */ }
            _dispatcher.Post(() =>
            {
                if (!_disposed) _bus.Broadcast(new WorkingTreeChangedMessage(repoId));
            });
        });
    }

    private void SetSelectedStaged(bool staged)
        => _marks.SetViewed([.. _cursor.SelectedPaths.Value], staged);

    private void SetAllStaged(bool staged)
    {
        var files = Files();
        if (files.Count == 0) return;
        var paths = new List<string>(files.Count);
        foreach (var f in files) paths.Add(f.Path);
        _marks.SetViewed(paths, staged);
    }

    // ---- commit ----

    private void DoCommit()
    {
        if (!_commitEnabled.Value) return;
        var message = _commitTitle.Value.Trim();
        if (message.Length == 0) return;

        // Up-front per-repo staged summary for the confirm step (5.4): only members with staged content
        // commit; the confirm dialog lists what each will capture.
        var staged = new List<(Guid RepoId, string Name, int Count)>();
        foreach (var m in _members)
        {
            var count = _localChanges.TryGetValue(m.RepoId, out var lc) ? lc.Staged.Count : 0;
            if (count > 0) staged.Add((m.RepoId, m.Repo.DisplayName, count));
        }
        if (staged.Count == 0) return;

        var branch = _branch;
        _bus.Broadcast(new ShowDialogMessage(onClose => new CommitChangeSetDialog
        {
            BranchName = branch,
            Staged = staged,
            OnConfirm = () =>
            {
                _ops.CommitInAll(staged.Select(x => x.RepoId).ToList(), message, branch);
                _commitTitle.Value = string.Empty;
            },
            OnClose = onClose,
        }));
    }

    // ---- derived helpers ----

    private bool AnySelected(Func<string, bool> predicate)
    {
        _ = _marks.Revision.Value;
        foreach (var p in _cursor.SelectedPaths.Value)
            if (predicate(p)) return true;
        return false;
    }

    private bool AnyStagedAnywhere()
    {
        _ = _marks.Revision.Value;
        foreach (var f in Files())
            if (_marks.HasStagedContent(f.Path)) return true;
        return false;
    }

    private ReviewHud BuildHud()
    {
        var files = Files();
        var staged = _cursor.CountMarked(files);
        return new ReviewHud(
            FilesViewed: staged,
            FilesTotal: files.Count,
            IsComplete: files.Count > 0 && staged >= files.Count);
    }

    private string BuildFilesStagedLabel()
    {
        var hud = _hud.Value;
        return hud.FilesTotal == 0
            ? string.Empty
            : _loc.Strings.Value.ReviewFilesStaged(hud.FilesViewed, hud.FilesTotal);
    }

    private IReadOnlyList<FileChange> Files() =>
        _details.RenderState.Value is CommitDetailsRenderState.Loaded l
            ? l.Details.Files
            : EmptyFiles;

    private Repo? FindRepo(Guid id)
    {
        var active = _registry.Active.Value;
        if (active != null && active.Id == id) return active;
        foreach (var r in _registry.Repos)
            if (r.Id == id) return r;
        return null;
    }

    private void RunBackground<T>(Func<T> work, Action<T> onResult)
    {
        Task.Run(() =>
        {
            var result = work();
            _dispatcher.Post(() =>
            {
                if (!_disposed) onResult(result);
            });
        });
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _commitEnabled.Dispose();
        _canUnstageSelected.Dispose();
        _canStageSelected.Dispose();
        _hasFiles.Dispose();
        _filesStagedLabel.Dispose();
        _filesFraction.Dispose();
        _hud.Dispose();
        _cheatsheetOpen.Dispose();
        _commitBusy.Dispose();
        _commitTitle.Dispose();
        _branchName.Dispose();
        _available.Dispose();
        _cursor.Dispose();
        _marks.Dispose();
        _details.Dispose();
    }
}
