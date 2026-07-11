using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Features.Review;

// What the cross-repo review window body should show: the loading state while the first aggregation
// resolves, a centered message on a whole-window failure, or the tree/diff split once files are in.
internal enum ChangeSetReviewContentKind { Loading, Message, Loaded }

/// <summary>
/// Implementation #3 of <see cref="IReviewSurfaceModel"/>: one review surface over the union of N
/// members' <c>base..head</c> diffs (Locked decision #2). Pinned to a <see cref="ChangeSetSession"/>,
/// it resolves each member's stack through the same <see cref="IReviewStackSource"/> a single-repo
/// review uses (per-repo base resolution), then drives its own <see cref="CommitDetailsViewModel"/>
/// via <see cref="CommitDetailsViewModel.ShowRanges"/> — one file tree grouped by repo, one stacked
/// diff surface, one aggregated progress meter, one keyboard loop. Marks route through the qualified
/// path back to each member's <c>(RepoId, HeadRef)</c> progress key (shared with single-repo review).
/// A member whose range fails to resolve renders as an inline error group, never a dead window; a
/// member's <see cref="RefsChangedMessage"/> reloads just that member.
/// </summary>
internal sealed class ChangeSetReviewViewModel : IReviewSurfaceModel, IDisposable
{
    private const int StackCap = 200;

    private readonly ChangeSetSession _session;
    private readonly IReviewStackSource _source;
    private readonly IUiDispatcher _dispatcher;
    private readonly CommitDetailsViewModel _details;
    private readonly ILocalizationService _loc;
    private readonly ChangeSetReviewedFiles _reviewedFiles;
    private readonly ReviewFileCursor _cursor;

    private readonly IReadOnlyDictionary<Guid, string> _repoKeys;
    // The last resolved load per member, in no order — DriveDetails re-emits sections in member order.
    private readonly Dictionary<Guid, ChangeSetMemberLoad> _loads = new();
    private readonly HashSet<Guid> _memberIds;

    private readonly State<bool> _everLoaded = new(false);
    // Bumps whenever a member's load lands, so the header's per-member base chips re-derive.
    private readonly State<int> _loadRevision = new(0);
    private readonly State<bool> _cheatsheetOpen = new(false);

    private readonly Derived<ReviewHud> _hud;
    private readonly Derived<float> _filesFraction;
    private readonly Derived<string> _filesViewedLabel;
    private readonly Derived<ChangeSetReviewContentKind> _contentKind;
    private readonly Derived<string> _placeholderText;
    private readonly List<IDisposable> _subscriptions = new();

    // Drops a stale full-reload's late result; a per-member reload rides the same value and applies
    // only while no newer full reload has superseded it.
    private int _generation;
    private bool _disposed;

    public ChangeSetReviewViewModel(
        ChangeSetSession session,
        IReviewStackSource source,
        IUiDispatcher dispatcher,
        CommitDetailsViewModel details,
        ILocalizationService loc,
        IMessageBus bus,
        IReviewProgressStore reviewProgress,
        IRepoRegistry registry)
    {
        _session = session;
        _source = source;
        _dispatcher = dispatcher;
        _details = details;
        _loc = loc;

        // Deterministic repo keys (de-duplicated display names) drive both the tree's top-level folders
        // (via the qualified paths ShowRanges builds) and the marks tracker's reverse resolution.
        var named = new List<(Guid, string)>(session.Members.Count);
        foreach (var m in session.Members)
            named.Add((m.RepoId, DisplayNameOf(registry, m.RepoId) ?? m.HeadLabel));
        _repoKeys = RepoQualifiedPaths.BuildKeys(named);

        var memberByKey = new Dictionary<string, (Guid RepoId, string HeadRef)>(StringComparer.Ordinal);
        foreach (var m in session.Members)
            memberByKey[_repoKeys[m.RepoId]] = (m.RepoId, m.HeadRef);
        _reviewedFiles = new ChangeSetReviewedFiles(reviewProgress, memberByKey);
        _cursor = new ReviewFileCursor(Files, _reviewedFiles);
        _memberIds = session.Members.Select(m => m.RepoId).ToHashSet();

        Title = loc.Strings.Value.ChangesetsReviewWindowTitle(session.Name);

        _hud = new Derived<ReviewHud>(BuildHud);
        _filesFraction = new Derived<float>(() => _hud.Value.FilesFraction);
        _filesViewedLabel = new Derived<string>(BuildFilesViewedLabel);
        _contentKind = new Derived<ChangeSetReviewContentKind>(BuildContentKind);
        _placeholderText = new Derived<string>(BuildPlaceholderText);

        // On each combined-list load, hand the marks tracker the new fingerprints and re-seed the cursor
        // — the same contract ReviewWindowViewModel keeps for a single range.
        _subscriptions.Add(_details.RenderState.Subscribe(r =>
        {
            if (r is not CommitDetailsRenderState.Loaded loaded) return;
            if (!_everLoaded.Value) _everLoaded.Value = true;
            _reviewedFiles.SetFingerprints(FingerprintsOf(loaded.Details.Files));
            _cursor.OnFilesLoaded(loaded.Details.Files);
        }));

        // A ref change in any member reshapes that member's range; reload just it and re-drive the
        // combined list (the other members' cached ranges are reused).
        _subscriptions.Add(bus.SubscribeScoped<RefsChangedMessage>(m =>
        {
            if (_memberIds.Contains(m.RepoId)) ReloadMember(m.RepoId);
        }));

        StartLoad();
    }

    public string Title { get; }
    public ChangeSetSession Session => _session;
    public int MemberCount => _session.Members.Count;

    public ReviewMarkKind MarkKind => ReviewMarkKind.Viewed;
    public IReviewedFileTracker ReviewedFiles => _reviewedFiles;
    public CommitDetailsViewModel Details => _details;

    public IReadable<ChangeSetReviewContentKind> ContentKind => _contentKind;
    public IReadable<string> PlaceholderText => _placeholderText;

    public IReadable<string?> ActiveFile => _cursor.ActiveFile;
    public IReadable<IReadOnlySet<string>> SelectedPaths => _cursor.SelectedPaths;
    public IReadable<string?> SelectionCursor => _cursor.SelectionCursor;
    public IReadable<ReviewHud> Hud => _hud;
    public IReadable<float> FilesFraction => _filesFraction;
    public IReadable<string> FilesViewedLabel => _filesViewedLabel;
    public IReadable<bool> CheatsheetOpen => _cheatsheetOpen;

    /// <summary>"N repos" for the header member-count chip.</summary>
    public string RepoCountLabel => _loc.Strings.Value.ChangesetsReviewRepos(MemberCount);

    /// <summary>Per-member base provenance for the header, re-derived when a member's load lands: the
    /// repo key with its resolved base label, or its error message.</summary>
    public IReadable<int> LoadRevision => _loadRevision;

    public IReadOnlyList<(string RepoKey, string Detail)> MemberSummaries()
    {
        var list = new List<(string, string)>(_session.Members.Count);
        foreach (var m in _session.Members)
        {
            var key = _repoKeys[m.RepoId];
            var detail = _loads.TryGetValue(m.RepoId, out var load)
                ? load switch
                {
                    ChangeSetMemberLoad.Ok ok => $"{ok.Stack.BaseLabel} → {ok.Stack.HeadLabel}",
                    ChangeSetMemberLoad.Failed f => f.Message,
                    _ => string.Empty,
                }
                : _loc.Strings.Value.ReviewBaseResolving;
            list.Add((key, detail));
        }
        return list;
    }

    public event Action<string>? ScrollToFileRequested
    {
        add => _cursor.ScrollToFileRequested += value;
        remove => _cursor.ScrollToFileRequested -= value;
    }

    public bool IsFileViewed(string path) => _reviewedFiles.IsViewed(path);
    public void ToggleFileViewed(string path) => _reviewedFiles.ToggleViewed(path);
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
        => BuildViewedContextMenuItems(_cursor.ResolveTargetPaths(path));

    public IReadOnlyList<RepoBarContextMenu.Item> BuildFolderContextMenuItems(IReadOnlyList<string> paths)
        => BuildViewedContextMenuItems(paths);

    private IReadOnlyList<RepoBarContextMenu.Item> BuildViewedContextMenuItems(IReadOnlyList<string> targets)
    {
        var s = _loc.Strings.Value;
        var unviewed = new List<string>(targets.Count);
        var viewed = new List<string>(targets.Count);
        foreach (var p in targets)
            (IsFileViewed(p) ? viewed : unviewed).Add(p);

        var items = new List<RepoBarContextMenu.Item>(2);
        if (unviewed.Count > 0)
            items.Add(new RepoBarContextMenu.Item(
                s.ReviewContextMarkViewed(unviewed.Count), () => _reviewedFiles.SetViewed(unviewed, true)));
        if (viewed.Count > 0)
            items.Add(new RepoBarContextMenu.Item(
                s.ReviewContextMarkNotViewed(viewed.Count), () => _reviewedFiles.SetViewed(viewed, false)));
        return items;
    }

    // Resolves every member from scratch (initial open); a full reload invalidates any in-flight
    // per-member reload via the generation bump.
    private void StartLoad()
    {
        var gen = ++_generation;
        var members = _session.Members;
        RunBackground(
            () => ChangeSetAggregator.LoadAll(_source, members, _repoKeys, StackCap),
            loads =>
            {
                if (gen != _generation) return;
                foreach (var load in loads) _loads[load.RepoId] = load;
                _loadRevision.Value++;
                DriveDetails();
            });
    }

    // Re-resolves one member after its ref change and re-drives the combined list, reusing the other
    // members' cached ranges. Stale-while-revalidate: the current list stays up until this returns, and
    // a full reload started meanwhile (generation moved) drops this result.
    private void ReloadMember(Guid repoId)
    {
        ReviewSession? member = null;
        foreach (var m in _session.Members)
            if (m.RepoId == repoId) { member = m; break; }
        if (member == null) return;

        var gen = _generation;
        var key = _repoKeys.TryGetValue(repoId, out var k) ? k : repoId.ToString("N");
        RunBackground(
            () => ChangeSetAggregator.LoadMember(_source, member, key, StackCap),
            load =>
            {
                if (gen != _generation) return;
                _loads[repoId] = load;
                _loadRevision.Value++;
                DriveDetails();
            });
    }

    // Projects the cached per-member loads (in the session's member order) into the details surface's
    // range sections — Ok members contribute their endpoints, Failed members an inline error group.
    private void DriveDetails()
    {
        var sections = new List<DetailsRangeSection>(_session.Members.Count);
        foreach (var m in _session.Members)
        {
            if (!_loads.TryGetValue(m.RepoId, out var load)) continue;
            sections.Add(load switch
            {
                ChangeSetMemberLoad.Ok ok =>
                    new DetailsRangeSection.Range(ok.RepoId, ok.RepoKey, ok.Stack.BaseSha, ok.Stack.HeadSha),
                ChangeSetMemberLoad.Failed f =>
                    new DetailsRangeSection.Failed(f.RepoId, f.RepoKey, f.Message),
                _ => new DetailsRangeSection.Failed(m.RepoId, _repoKeys[m.RepoId], string.Empty),
            });
        }
        _details.ShowRanges(sections);
    }

    // Off-thread work with a UI-thread continuation — the RunBackground convention, hand-rolled because
    // this surface (like WorkingTreeReviewViewModel) is not a ViewModelBase. The aggregator never
    // throws (it folds member failures), so no try/catch is needed here.
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

    private ChangeSetReviewContentKind BuildContentKind()
    {
        if (!_everLoaded.Value) return ChangeSetReviewContentKind.Loading;
        return _details.RenderState.Value switch
        {
            CommitDetailsRenderState.Placeholder => ChangeSetReviewContentKind.Message,
            // A reload drops the details to Loading; hold the current split up (stale-while-revalidate).
            _ => ChangeSetReviewContentKind.Loaded,
        };
    }

    private string BuildPlaceholderText()
    {
        if (_details.RenderState.Value is CommitDetailsRenderState.Placeholder p) return p.Text;
        return _loc.Strings.Value.ReviewLoading;
    }

    private ReviewHud BuildHud()
    {
        var files = Files();
        var viewed = _cursor.CountMarked(files);
        return new ReviewHud(
            FilesViewed: viewed,
            FilesTotal: files.Count,
            IsComplete: files.Count > 0 && viewed >= files.Count);
    }

    private string BuildFilesViewedLabel()
    {
        var files = Files();
        if (files.Count == 0) return string.Empty;
        return _loc.Strings.Value.ReviewFilesViewed(_cursor.CountMarked(files), files.Count);
    }

    private IReadOnlyList<FileChange> Files() =>
        _details.RenderState.Value is CommitDetailsRenderState.Loaded l
            ? l.Details.Files
            : Array.Empty<FileChange>();

    private static IReadOnlyDictionary<string, string?> FingerprintsOf(IReadOnlyList<FileChange> files)
    {
        var map = new Dictionary<string, string?>(files.Count, StringComparer.Ordinal);
        foreach (var f in files) map[f.Path] = f.ContentId;
        return map;
    }

    private static string? DisplayNameOf(IRepoRegistry registry, Guid repoId)
    {
        var active = registry.Active.Value;
        if (active != null && active.Id == repoId) return active.DisplayName;
        foreach (var r in registry.Repos)
            if (r.Id == repoId) return r.DisplayName;
        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _placeholderText.Dispose();
        _contentKind.Dispose();
        _filesViewedLabel.Dispose();
        _filesFraction.Dispose();
        _hud.Dispose();
        _cheatsheetOpen.Dispose();
        _loadRevision.Dispose();
        _everLoaded.Dispose();
        _cursor.Dispose();
        _reviewedFiles.Dispose();
        _details.Dispose();
    }
}
