using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Submodules;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Commits;

public abstract record CommitDetailsRenderState
{
    // No-selection and error states (carry centered text); Loading shows the structural skeleton.
    public sealed record Placeholder(string Text) : CommitDetailsRenderState;
    public sealed record Loading : CommitDetailsRenderState;
    public sealed record Loaded(CommitDetails Details) : CommitDetailsRenderState;
}

internal sealed record CommitDetailsState(
    CommitDetailsRenderState Render,
    string? SelectedPath,
    FileViewMode ViewMode);

/// <summary>
/// One member section of a cross-repo combined-range surface (<see cref="CommitDetailsViewModel.ShowRanges"/>):
/// either a member repo's <c>base..head</c> <see cref="Range"/> (its files are qualified under
/// <see cref="RepoKey"/> in the tree), or an inline load <see cref="Failed"/> rendered as that repo's
/// error group instead of sinking the whole window.
/// </summary>
public abstract record DetailsRangeSection(Guid RepoId, string RepoKey)
{
    public sealed record Range(Guid RepoId, string RepoKey, string BaseSha, string HeadSha)
        : DetailsRangeSection(RepoId, RepoKey);

    public sealed record Failed(Guid RepoId, string RepoKey, string Message)
        : DetailsRangeSection(RepoId, RepoKey);
}

/// <summary>
/// One member section of a cross-repo combined working-tree surface (<see cref="CommitDetailsViewModel.ShowWorkingTrees"/>):
/// either a member repo's working-tree <see cref="Files"/> (each diffs HEAD→disk and is qualified under
/// <see cref="RepoKey"/> in the tree), or an inline load <see cref="Failed"/> rendered as that repo's
/// error group. The Phase-5.1 twin of <see cref="DetailsRangeSection"/> — the files are pushed in by the
/// host (the aggregating view model already holds them) rather than loaded from a sha, so it is synchronous.
/// </summary>
public abstract record DetailsWorkingTreeSection(Guid RepoId, string RepoKey)
{
    public sealed record Files(Guid RepoId, string RepoKey, IReadOnlyList<FileChange> Changes)
        : DetailsWorkingTreeSection(RepoId, RepoKey);

    public sealed record Failed(Guid RepoId, string RepoKey, string Message)
        : DetailsWorkingTreeSection(RepoId, RepoKey);
}

internal sealed class CommitDetailsViewModel : ViewModelBase<CommitDetailsState>
{
    private readonly IGitService _gitService;
    private readonly IRepoRegistry _registry;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly PreferencesService _preferences;
    private string? _currentSha;
    // Non-null only in the Review window's Combined mode: the range base. When set, opened files are
    // range tabs (base→head) rather than commit-vs-parent. Null for every commit/History selection.
    private string? _currentBaseSha;
    private Guid _currentRepoId;
    // Set only by ShowWorkingTree: opened files diff HEAD→disk and the file list is pushed in by the
    // host rather than loaded from a sha.
    private bool _workingTree;

    // Non-null only in the cross-repo review's Ranges mode: qualified path → the member repo + bare
    // path + endpoints its diff resolves against. When set, SelectFile/CreateFileDiff route per-file
    // through it (the surface spans N repos) instead of the single _currentRepoId/_currentBaseSha pin.
    // The qualification (repo-key prefixing + this resolver) lives entirely here; the diff widgets and
    // the tab strip stay repo-blind. Null for every single-repo commit/range/working-tree surface.
    private IReadOnlyDictionary<string, QualifiedFileRef>? _rangeFiles;

    // Non-null only in the cross-repo working-tree's WorkingTrees mode (Phase 5.1): qualified path → the
    // member repo + bare path its HEAD→disk diff resolves against. The working-tree twin of _rangeFiles;
    // when set, SelectFile/CreateFileDiff route per-file through it. Mutually exclusive with the other
    // modes — every Show* entry nulls it, and it nulls the others.
    private IReadOnlyDictionary<string, QualifiedWorkingRef>? _workingTrees;

    private readonly record struct QualifiedFileRef(Guid RepoId, string Path, string BaseSha, string HeadSha);

    private readonly record struct QualifiedWorkingRef(Guid RepoId, string Path);

    private string DefaultPlaceholder => _loc.Strings.Value.CommitsDetailsNoSelection;

    public IReadable<CommitDetailsRenderState> RenderState { get; }
    public IReadable<string?> SelectedPath { get; }
    public IReadable<FileViewMode> ViewMode { get; }
    public Command ToggleViewMode { get; }

    // Open file tabs, in tab order. The "Details" tab (commit metadata + file list) is implicit and
    // always present as the leftmost tab; SelectedPath == null means it is the active tab. Each entry
    // owns its own DiffViewModel, so switching tabs preserves each file's loaded diff and scroll.
    public ObservableList<CommitFileTab> OpenTabs { get; } = new();

    public CommitDetailsViewModel(
        IGitService gitService,
        IRepoRegistry registry,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        PreferencesService preferences,
        bool subscribeToSelection = true)
        : base(dispatcher, new CommitDetailsState(
            new CommitDetailsRenderState.Placeholder(loc.Strings.Value.CommitsDetailsNoSelection),
            null, preferences.Current.FileViewMode))
    {
        _gitService = gitService;
        _registry = registry;
        _bus = bus;
        _loc = loc;
        _preferences = preferences;

        RenderState = Slice(s => s.Render);
        SelectedPath = Slice(s => s.SelectedPath);
        ViewMode = Slice(s => s.ViewMode);
        ToggleViewMode = new Command(DoToggleViewMode);

        // The History pane follows commit selection over the bus; the Review window drives its own
        // instance directly via Show()/Clear() and opts out so it never reacts to History's selection.
        if (subscribeToSelection)
            Subscriptions.Add(_bus.SubscribeScoped<CommitSelectedMessage>(OnCommitSelected));
    }

    /// <summary>The active tab's diff view model, or null when the implicit Details tab is active.</summary>
    public DiffViewModel? ActiveDiff => FindTab(State.Value.SelectedPath)?.Diff;

    // Shares the global tree/flat preference with the Local Changes panels so the choice is
    // consistent app-wide and persists across launches.
    private void DoToggleViewMode()
    {
        var next = State.Value.ViewMode == FileViewMode.Flat ? FileViewMode.Tree : FileViewMode.Flat;
        _preferences.SetFileViewMode(next);
        Update(s => s with { ViewMode = next });
    }

    public override void Dispose()
    {
        CloseAllTabs();
        base.Dispose();
    }

    /// <summary>Opens the file in a new tab — or focuses its existing tab — and makes it active.</summary>
    public void SelectFile(string path)
    {
        if (_rangeFiles != null)
        {
            if (FindTab(path) == null && _rangeFiles.TryGetValue(path, out var r))
                OpenTabs.Add(new CommitFileTab(
                    r.Path, r.HeadSha, r.RepoId, _registry, _gitService, Dispatcher, _bus, _loc,
                    baseSha: r.BaseSha, displayPath: path));
            Update(s => s with { SelectedPath = path });
            return;
        }
        if (_workingTrees != null)
        {
            if (FindTab(path) == null && _workingTrees.TryGetValue(path, out var w))
                OpenTabs.Add(CommitFileTab.ForWorkingTree(
                    w.Path, w.RepoId, _registry, _gitService, Dispatcher, _bus, _loc, displayPath: path));
            Update(s => s with { SelectedPath = path });
            return;
        }
        if (string.IsNullOrEmpty(_currentSha)) return;
        if (FindTab(path) == null)
            OpenTabs.Add(new CommitFileTab(path, _currentSha, _currentRepoId, _registry, _gitService, Dispatcher, _bus, _loc, _currentBaseSha));
        Update(s => s with { SelectedPath = path });
    }

    /// <summary>
    /// Creates a standalone diff handle for a file of the current commit/range — the same target
    /// resolution an open tab gets, but never added to <see cref="OpenTabs"/>. The review window's
    /// stacked diff list creates these lazily per file; the caller owns disposal. Null when no
    /// commit/range is loaded.
    /// </summary>
    public CommitFileTab? CreateFileDiff(string path)
    {
        if (_rangeFiles != null)
        {
            // Cross-repo Ranges mode: resolve the qualified path to its member repo + bare path so the
            // diff loads against that member's range. Error-group rows have no entry and get no diff.
            if (!_rangeFiles.TryGetValue(path, out var r)) return null;
            return new CommitFileTab(
                r.Path, r.HeadSha, r.RepoId, _registry, _gitService, Dispatcher, _bus, _loc,
                baseSha: r.BaseSha, displayPath: path);
        }
        if (_workingTrees != null)
        {
            // Cross-repo WorkingTrees mode: resolve the qualified path to its member repo + bare path so
            // the diff loads HEAD→disk in that member. Error-group rows have no entry and get no diff.
            if (!_workingTrees.TryGetValue(path, out var w)) return null;
            return CommitFileTab.ForWorkingTree(
                w.Path, w.RepoId, _registry, _gitService, Dispatcher, _bus, _loc, displayPath: path);
        }
        if (_workingTree)
            return CommitFileTab.ForWorkingTree(path, _currentRepoId, _registry, _gitService, Dispatcher, _bus, _loc);
        if (string.IsNullOrEmpty(_currentSha)) return null;
        return new CommitFileTab(path, _currentSha, _currentRepoId, _registry, _gitService, Dispatcher, _bus, _loc, _currentBaseSha);
    }

    /// <summary>Switches the active tab. A null path activates the implicit Details tab.</summary>
    public void ActivateTab(string? path) => Update(s => s with { SelectedPath = path });

    /// <summary>
    /// Enters the loading state immediately, before the next range is resolved — the Review window's
    /// optimistic base switch flips its columns to their loading treatment the instant the reviewer
    /// picks a new base, without waiting for the range resolution to return. A following
    /// <see cref="ShowRange"/> supplies the resolved endpoints.
    /// </summary>
    public void EnterLoading() =>
        Update(s => s with { SelectedPath = null, Render = new CommitDetailsRenderState.Loading() });

    /// <summary>Closes a file tab and disposes its diff. If it was active, the neighbouring tab (or
    /// the Details tab when none remain) becomes active.</summary>
    public void CloseTab(string path)
    {
        var index = IndexOfTab(path);
        if (index < 0) return;
        var tab = OpenTabs[index];
        var wasActive = State.Value.SelectedPath == path;
        OpenTabs.RemoveAt(index);
        tab.Dispose();
        if (!wasActive) return;
        var next = OpenTabs.Count == 0 ? null : OpenTabs[Math.Min(index, OpenTabs.Count - 1)].Path;
        Update(s => s with { SelectedPath = next });
    }

    private CommitFileTab? FindTab(string? path)
    {
        if (path == null) return null;
        foreach (var tab in OpenTabs)
            if (tab.Path == path) return tab;
        return null;
    }

    private int IndexOfTab(string path)
    {
        for (var i = 0; i < OpenTabs.Count; i++)
            if (OpenTabs[i].Path == path) return i;
        return -1;
    }

    private void CloseAllTabs()
    {
        if (OpenTabs.Count == 0) return;
        foreach (var tab in OpenTabs) tab.Dispose();
        OpenTabs.Clear();
    }

    private void OnCommitSelected(CommitSelectedMessage msg)
    {
        if (string.IsNullOrEmpty(msg.Sha))
        {
            Clear();
            return;
        }
        // The History pane follows only the active repo's selection — guard stale messages from a
        // repo it has since switched away from.
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != msg.RepoId) return;
        Show(msg.RepoId, msg.Sha);
    }

    /// <summary>Resets to the no-selection placeholder, closing all tabs and cancelling any in-flight load.</summary>
    public void Clear()
    {
        Gen.Bump();
        _currentSha = null;
        _currentBaseSha = null;
        _workingTree = false;
        _rangeFiles = null;
        _workingTrees = null;
        CloseAllTabs();
        Update(s => s with
        {
            SelectedPath = null,
            Render = new CommitDetailsRenderState.Placeholder(DefaultPlaceholder),
        });
    }

    /// <summary>
    /// Loads and shows the given commit's details. The repo is resolved by id (not assumed active),
    /// so a pinned surface like the Review window keeps working when it isn't the main window's
    /// active repo. No-ops when the repo isn't open.
    /// </summary>
    public void Show(Guid repoId, string sha)
    {
        var repo = ResolveRepo(repoId);
        if (repo == null) return;

        _currentSha = sha;
        _currentBaseSha = null;
        _workingTree = false;
        _rangeFiles = null;
        _workingTrees = null;
        _currentRepoId = repoId;
        CloseAllTabs();
        Update(s => s with
        {
            SelectedPath = null,
            Render = new CommitDetailsRenderState.Loading(),
        });

        RunBackground<CommitDetailsRenderState>(
            work: () =>
            {
                var fetched = _gitService.LoadDetails(repo, sha);
                if (fetched is Fetched<CommitDetails>.Failed failed)
                    return (new CommitDetailsRenderState.Placeholder(failed.Message), null);

                var details = ((Fetched<CommitDetails>.Ok)fetched).Value;
                var pointerChanges = _gitService.GetSubmodulePointerChanges(repo, sha);
                if (pointerChanges.Count > 0)
                    details = MergePointerChanges(details, pointerChanges);
                return (new CommitDetailsRenderState.Loaded(details), null);
            },
            onResult: (result, error) =>
                Update(s => s with
                {
                    Render = error != null ? new CommitDetailsRenderState.Placeholder(error) : result!,
                }));
    }

    /// <summary>
    /// Loads and shows the combined net diff of a review range — base→head as one file list — for the
    /// Review window's Combined mode. The History pane never calls this. The Details tab renders a
    /// synthesized range summary; opening a file diffs base→head (<see cref="DiffSide.Range"/>).
    /// </summary>
    public void ShowRange(Guid repoId, string baseSha, string headSha)
    {
        var repo = ResolveRepo(repoId);
        if (repo == null) return;

        _currentSha = headSha;
        _currentBaseSha = baseSha;
        _workingTree = false;
        _rangeFiles = null;
        _workingTrees = null;
        _currentRepoId = repoId;
        CloseAllTabs();
        Update(s => s with
        {
            SelectedPath = null,
            Render = new CommitDetailsRenderState.Loading(),
        });

        RunBackground<CommitDetailsRenderState>(
            work: () =>
            {
                var fetched = _gitService.LoadRangeFiles(repo, baseSha, headSha);
                if (fetched is Fetched<IReadOnlyList<FileChange>>.Failed failed)
                    return (new CommitDetailsRenderState.Placeholder(failed.Message), null);

                var files = ((Fetched<IReadOnlyList<FileChange>>.Ok)fetched).Value;
                return (new CommitDetailsRenderState.Loaded(BuildRangeDetails(repoId, baseSha, headSha, files)), null);
            },
            onResult: (result, error) =>
                Update(s => s with
                {
                    Render = error != null ? new CommitDetailsRenderState.Placeholder(error) : result!,
                }));
    }

    /// <summary>
    /// Widens <see cref="ShowRange"/> to N members for the cross-repo review surface: each section's
    /// <c>base..head</c> files are loaded, repo-qualified (Locked decision #4), and merged into one file
    /// list grouped by repo (the repo key is the tree's top-level folder). A member whose range fails to
    /// load renders as an inline error group under its repo folder — the window stays alive. Opening a
    /// file resolves back through <see cref="_rangeFiles"/> to the owning member's bare path + endpoints,
    /// so the diff widgets never learn about repos. Existing single-repo <see cref="ShowRange"/> callers
    /// are untouched (bare paths, one repo pin).
    /// </summary>
    public void ShowRanges(IReadOnlyList<DetailsRangeSection> sections)
    {
        _workingTree = false;
        _workingTrees = null;
        _currentSha = null;
        _currentBaseSha = null;
        // Enter Ranges mode immediately so any diff handle minted before the load returns resolves
        // per-file (empty until the resolved map lands) rather than through the stale single-repo pin.
        _rangeFiles = new Dictionary<string, QualifiedFileRef>();
        CloseAllTabs();
        Update(s => s with
        {
            SelectedPath = null,
            Render = new CommitDetailsRenderState.Loading(),
        });

        RunBackground<RangesLoad>(
            work: () =>
            {
                var resolver = new Dictionary<string, QualifiedFileRef>(StringComparer.Ordinal);
                var files = new List<FileChange>();
                var keyParts = new List<string>(sections.Count);

                foreach (var section in sections)
                {
                    switch (section)
                    {
                        case DetailsRangeSection.Range r:
                            var repo = ResolveRepo(r.RepoId);
                            if (repo == null)
                            {
                                AddErrorRow(files, r.RepoKey, _loc.Strings.Value.ReviewErrorRepoUnavailable);
                                keyParts.Add($"{r.RepoKey}:norepo");
                                break;
                            }
                            var fetched = _gitService.LoadRangeFiles(repo, r.BaseSha, r.HeadSha);
                            if (fetched is Fetched<IReadOnlyList<FileChange>>.Failed failed)
                            {
                                AddErrorRow(files, r.RepoKey, failed.Message);
                                keyParts.Add($"{r.RepoKey}:err");
                                break;
                            }
                            foreach (var f in ((Fetched<IReadOnlyList<FileChange>>.Ok)fetched).Value)
                            {
                                var qualified = RepoQualifiedPaths.Qualify(r.RepoKey, f.Path);
                                files.Add(f with
                                {
                                    Path = qualified,
                                    OldPath = f.OldPath == null ? null : RepoQualifiedPaths.Qualify(r.RepoKey, f.OldPath),
                                });
                                resolver[qualified] = new QualifiedFileRef(r.RepoId, f.Path, r.BaseSha, r.HeadSha);
                            }
                            keyParts.Add($"{r.RepoKey}:{ReviewFileKey.ForRange(r.BaseSha, r.HeadSha)}");
                            break;

                        case DetailsRangeSection.Failed fl:
                            AddErrorRow(files, fl.RepoKey, fl.Message);
                            keyParts.Add($"{fl.RepoKey}:failed");
                            break;
                    }
                }

                var sha = "ranges:" + string.Join("|", keyParts);
                var render = new CommitDetailsRenderState.Loaded(BuildRangesDetails(sha, files));
                return (new RangesLoad(render, resolver), null);
            },
            onResult: (result, error) =>
            {
                if (error != null || result == null)
                {
                    _rangeFiles = new Dictionary<string, QualifiedFileRef>();
                    Update(s => s with { Render = new CommitDetailsRenderState.Placeholder(error ?? DefaultPlaceholder) });
                    return;
                }
                _rangeFiles = result.Resolver;
                Update(s => s with { Render = result.Render });
            });
    }

    private sealed record RangesLoad(
        CommitDetailsRenderState Render,
        IReadOnlyDictionary<string, QualifiedFileRef> Resolver);

    // A member's failed range as a single red row under its repo folder — the tree stays legible and
    // the other members still review. The row carries no resolver entry, so it has no diff to open.
    private void AddErrorRow(List<FileChange> files, string repoKey, string message)
    {
        var label = _loc.Strings.Value.ChangesetsReviewMemberFailed(message);
        files.Add(new FileChange(RepoQualifiedPaths.Qualify(repoKey, label), null, FileChangeStatus.Conflicted));
    }

    // The synthetic CommitDetails for a cross-repo combined surface: the same shape ShowRange builds,
    // but its Sha is the aggregate range key (all members' endpoints) so a member's endpoints moving
    // rebuilds the list, and RepoId is empty because the surface spans repos (per-file identity lives
    // in the qualified paths, not here).
    private CommitDetails BuildRangesDetails(string sha, IReadOnlyList<FileChange> files)
    {
        var s = _loc.Strings.Value;
        return new CommitDetails(
            RepoId: Guid.Empty,
            Sha: sha,
            AuthorName: s.ReviewCombinedDiffTitle,
            AuthorEmail: string.Empty,
            AuthorWhen: default,
            CommitterName: string.Empty,
            CommitterEmail: string.Empty,
            CommitterWhen: default,
            Message: s.ReviewCombinedSummary(files.Count),
            MessageShort: s.ReviewCombinedSummary(files.Count),
            ParentShas: Array.Empty<string>(),
            Files: files);
    }

    /// <summary>
    /// Shows the working tree's changed files as one list, for the working-tree review surface.
    /// Unlike <see cref="Show"/> / <see cref="ShowRange"/> the files are pushed in by the host (which
    /// already has them from the working-tree snapshot), so this is synchronous — and it deliberately
    /// leaves the open tabs alone: the working tree changes on every editor save, and tearing the
    /// surface down each time would throw away the reviewer's scroll and loaded diffs. Opened files
    /// diff HEAD→disk (<see cref="DiffSide.WorkingTree"/>), so staging never re-targets them.
    /// </summary>
    public void ShowWorkingTree(Guid repoId, IReadOnlyList<FileChange> files)
    {
        if (ResolveRepo(repoId) == null) return;

        // A cross-repo switch has nothing in common with what's on screen; drop the tabs.
        if (_rangeFiles != null || _workingTrees != null || !_workingTree || _currentRepoId != repoId) CloseAllTabs();

        _workingTree = true;
        _rangeFiles = null;
        _workingTrees = null;
        _currentSha = null;
        _currentBaseSha = null;
        _currentRepoId = repoId;

        var s = _loc.Strings.Value;
        var details = new CommitDetails(
            RepoId: repoId,
            Sha: CommitFileTab.WorkingTreeKey(repoId),
            AuthorName: s.ReviewWorkingTreeTitle,
            AuthorEmail: string.Empty,
            AuthorWhen: default,
            CommitterName: string.Empty,
            CommitterEmail: string.Empty,
            CommitterWhen: default,
            Message: s.ReviewCombinedSummary(files.Count),
            MessageShort: s.ReviewCombinedSummary(files.Count),
            ParentShas: ["HEAD"],
            Files: files);

        Update(state => state with { Render = new CommitDetailsRenderState.Loaded(details) });
    }

    /// <summary>
    /// Widens <see cref="ShowWorkingTree"/> to N members for the cross-repo working-tree surface (Phase
    /// 5.1, the twin of <see cref="ShowRanges"/>): each member's changed files (already loaded by the
    /// aggregating host) are repo-qualified (Locked decision #4) and merged into one file list grouped by
    /// repo, with a <c>qualified → (member repo, bare path)</c> resolver so opening a file diffs HEAD→disk
    /// in the owning member. A member whose load failed renders as an inline error group under its repo
    /// folder. Synchronous and — like <see cref="ShowWorkingTree"/> — it deliberately keeps the open tabs
    /// alive across the frequent working-tree refreshes, dropping them only when switching in from another
    /// surface. Existing single-repo <see cref="ShowWorkingTree"/> callers are untouched.
    /// </summary>
    public void ShowWorkingTrees(IReadOnlyList<DetailsWorkingTreeSection> sections)
    {
        // Switching in from any other surface has nothing in common on screen — drop the tabs. Staying in
        // WorkingTrees mode across a refresh keeps them (the working tree changes on every editor save).
        if (_workingTrees == null) CloseAllTabs();

        _workingTree = false;
        _currentSha = null;
        _currentBaseSha = null;
        _currentRepoId = Guid.Empty;

        var resolver = new Dictionary<string, QualifiedWorkingRef>(StringComparer.Ordinal);
        var files = new List<FileChange>();
        var keyParts = new List<string>(sections.Count);

        foreach (var section in sections)
        {
            switch (section)
            {
                case DetailsWorkingTreeSection.Files f:
                    foreach (var c in f.Changes)
                    {
                        var qualified = RepoQualifiedPaths.Qualify(f.RepoKey, c.Path);
                        files.Add(c with
                        {
                            Path = qualified,
                            OldPath = c.OldPath == null ? null : RepoQualifiedPaths.Qualify(f.RepoKey, c.OldPath),
                        });
                        resolver[qualified] = new QualifiedWorkingRef(f.RepoId, c.Path);
                    }
                    keyParts.Add($"{f.RepoKey}:{f.Changes.Count}");
                    break;

                case DetailsWorkingTreeSection.Failed fl:
                    AddErrorRow(files, fl.RepoKey, fl.Message);
                    keyParts.Add($"{fl.RepoKey}:failed");
                    break;
            }
        }

        _workingTrees = resolver;

        var s = _loc.Strings.Value;
        var details = new CommitDetails(
            RepoId: Guid.Empty,
            Sha: "working-trees:" + string.Join("|", keyParts),
            AuthorName: s.ReviewWorkingTreeTitle,
            AuthorEmail: string.Empty,
            AuthorWhen: default,
            CommitterName: string.Empty,
            CommitterEmail: string.Empty,
            CommitterWhen: default,
            Message: s.ReviewCombinedSummary(files.Count),
            MessageShort: s.ReviewCombinedSummary(files.Count),
            ParentShas: ["HEAD"],
            Files: files);

        Update(state => state with { Render = new CommitDetailsRenderState.Loaded(details) });
    }

    // A synthetic CommitDetails for a combined range so the shared details view renders it unchanged:
    // a labelled "Combined diff" header, a file-count subject, and base shown as the parent. Sha is
    // the shared "base..head" range key, so the Changes list's per-file Viewed marks land under the
    // same identity the open tabs and diff headers use.
    private CommitDetails BuildRangeDetails(Guid repoId, string baseSha, string headSha, IReadOnlyList<FileChange> files)
    {
        var s = _loc.Strings.Value;
        return new CommitDetails(
            RepoId: repoId,
            Sha: ReviewFileKey.ForRange(baseSha, headSha),
            AuthorName: s.ReviewCombinedDiffTitle,
            AuthorEmail: string.Empty,
            AuthorWhen: default,
            CommitterName: string.Empty,
            CommitterEmail: string.Empty,
            CommitterWhen: default,
            Message: s.ReviewCombinedSummary(files.Count),
            MessageShort: s.ReviewCombinedSummary(files.Count),
            ParentShas: new[] { baseSha },
            Files: files);
    }

    private Repo? ResolveRepo(Guid repoId)
    {
        var active = _registry.Active.Value;
        if (active != null && active.Id == repoId) return active;
        foreach (var r in _registry.Repos)
            if (r.Id == repoId) return r;
        return null;
    }

    private static CommitDetails MergePointerChanges(CommitDetails details, IReadOnlyList<SubmodulePointerChange> changes)
    {
        var byPath = new Dictionary<string, SubmodulePointerChange>(StringComparer.Ordinal);
        foreach (var c in changes) byPath[c.Path] = c;

        var newFiles = new List<FileChange>(details.Files.Count + changes.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in details.Files)
        {
            if (byPath.TryGetValue(f.Path, out var pc))
            {
                newFiles.Add(new FileChange(f.Path, f.OldPath, FileChangeStatus.Submodule)
                {
                    PointerChange = pc,
                });
                seen.Add(f.Path);
            }
            else
            {
                newFiles.Add(f);
            }
        }
        foreach (var c in changes)
        {
            if (seen.Contains(c.Path)) continue;
            newFiles.Add(new FileChange(c.Path, null, FileChangeStatus.Submodule)
            {
                PointerChange = c,
            });
        }
        return details with { Files = newFiles };
    }
}
